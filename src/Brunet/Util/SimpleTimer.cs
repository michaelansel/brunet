/*
Copyright (C) 2008  David Wolinsky <davidiw@ufl.edu>, University of Florida

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using Brunet.Concurrent;

using System.Threading;
using System.Collections.Generic;
using System;

#if BRUNET_NUNIT
using NUnit.Framework;
using System.Collections;
#endif
using Brunet.Collections;

namespace Brunet.Util {
  /// <summary>A very simple timer, single-threaded blocking timer, inspired
  /// by Mono's System.Threading.Timer. </summary>
  public class SimpleTimer : IComparable<SimpleTimer> {
    protected readonly WaitCallback _callback;
    protected readonly IAction _action;
    protected readonly object _state;
    /// <summary>How often the timer will be called.</summary>
    public int Period { get { return _period_ms; } }
    protected readonly int _period_ms;
    protected bool _stopped;
    protected long _next_run;
    protected readonly int _first_run;

    protected readonly static object _sync;
    protected readonly static Heap<SimpleTimer> _timers;
    protected static int _running;
    protected readonly static AutoResetEvent _re;
    protected readonly static Thread _thread;
    protected int _started;

    static SimpleTimer()
    {
      _sync = new object();
      _timers = new Heap<SimpleTimer>();
#if !BRUNET_SIMULATOR
      _re = new AutoResetEvent(false);
      _thread = new Thread(TimerThread);
      _thread.IsBackground = true;
      _thread.Start();
#endif
    }

#if BRUNET_SIMULATOR
    public static long Minimum {
      get {
        long minimum = long.MaxValue;
        if(!_timers.Empty) {
          minimum = _timers.Peek()._next_run;
        }
        return minimum;
      }
    }

    public static void RunSteps(int ms) {
      RunSteps(ms, true);
    }

    public static void RunSteps(int ms, bool log) {
      long cycle = DateTime.UtcNow.Ticks;
      long diff = cycle + (ms * TimeSpan.TicksPerMillisecond);

      System.DateTime last = System.DateTime.UtcNow;
      while(diff >= cycle) {
        System.DateTime now = System.DateTime.UtcNow;
        if(last.AddSeconds(5) < now) {
          last = now;
          if(log) {
            Console.WriteLine(now + ": " + DateTime.UtcNow);
          }
        }
        Brunet.DateTime.SetTime(cycle);
        cycle = SimpleTimer.Run();
      }

      Brunet.DateTime.SetTime(diff);
    }

    public static void RunStep() {
      long next = SimpleTimer.Minimum;
      Brunet.DateTime.SetTime(next);
      SimpleTimer.Run();
    }
#endif

    /// <summary>Process all applicable events.  Called by TimerThread for
    /// non-simulation and directly for Simulation of real time.</summary>
#if BRUNET_SIMULATOR
    public static long Run()
#else
    protected static long Run()
#endif
    {
      long min_next_run = long.MaxValue;
      long ticks = DateTime.UtcNow.Ticks;
      SimpleTimer timer = null;
      _running = 0;
      while(true) {
        bool stopped = false;
#if !BRUNET_SIMULATOR
        lock(_sync) {
#endif
          if(_timers.Empty) {
            break;
          }

          timer = _timers.Peek();
          if(timer._next_run > ticks) {
            min_next_run = timer._next_run;
            break;
          }

          _timers.Pop();
          if(timer._stopped) {
            continue;
          }
          if(timer._period_ms > 0 && timer._period_ms != Timeout.Infinite) {
            timer.Update(timer._period_ms, timer._period_ms);
          } else {
            stopped = true;
          }
#if !BRUNET_SIMULATOR
        }
#endif

        try {
          if(timer._callback != null) {
            timer._callback(timer._state);
          } else {
            timer._action.Start();
          }
        } catch (Exception e) {
          //ProtocolLog is not in Brunet.Util:
          Console.WriteLine(e);
          //ProtocolLog.WriteIf(ProtocolLog.Exceptions, e.ToString());
        }

        if(stopped) {
          timer.Stop();
        }
      }

      return min_next_run;
    }

    /// <summary>Wait for the next event.</summary>
    protected static void TimerThread() {
      while(true) {
        long next_run = Run();
        if(next_run == long.MaxValue) {
          _re.WaitOne(Timeout.Infinite, true);
        } else {
          long diff = next_run - DateTime.UtcNow.Ticks;
          if(diff <= 0) {
            continue;
          }
          _re.WaitOne((int) (diff / TimeSpan.TicksPerMillisecond), true);
        }
      }
    }

    /// <summary>Creates a new timer.</summary>
    public SimpleTimer(WaitCallback callback, object state, int dueTime, int period) :
      this(dueTime, period)

    {
      _callback = callback;
      _state = state;
    }

    /// <summary>Creates a new timer.</summary>
    public SimpleTimer(IAction action, int dueTime, int period) :
      this(dueTime, period)
    {
      _action = action;
    }

    protected SimpleTimer(int dueTime, int period)
    {
      if(dueTime < -1) {
        throw new ArgumentOutOfRangeException("dueTime");
      } else if(period < -1) {
        throw new ArgumentOutOfRangeException("period");
      }

      _first_run = dueTime;
      _period_ms = period;
      _stopped = false;
      _started = 0;
    }

    /// <summary>Creates and starts a timer.</summary>
    public static SimpleTimer Enqueue(WaitCallback callback, object state,
        int dueTime, int period)
    {
      SimpleTimer st = new SimpleTimer(callback, state, dueTime, period);
      st.Start();
      return st;
    }

    /// <summary>Creates and starts a timer.</summary>
    public static SimpleTimer Enqueue(IAction action, int dueTime, int period)
    {
      SimpleTimer st = new SimpleTimer(action, dueTime, period);
      st.Start();
      return st;
    }

    /// <summary>Puts the timer in the queue to be executed!  Can only be
    /// started once!</summary>
    public void Start()
    {
      if(Interlocked.Exchange(ref _started, 1) == 1) {
        throw new Exception("Already started!");
      }

      Update(_first_run, _period_ms);
    }

    /// <summary>Updates a timer for its first or next run.</summary>
    protected void Update(int dueTime, int period)
    {
      long now = DateTime.UtcNow.Ticks;
      if(dueTime == Timeout.Infinite) {
        throw new Exception("There must be a due time!");
      } else {
        _next_run = dueTime * TimeSpan.TicksPerMillisecond + now;
      }

#if BRUNET_SIMULATOR
      _timers.Add(this);
#else
      bool first = false;
      lock(_sync) {
        first = _timers.Add(this);
      }
      // If we're in the simulator, we don't use the AutoResetEvent
      if(first) {
        _re.Set();
      }
#endif
    }

    /// <summary>Don't call the event, I'm done.</summary>
    public void Stop()
    {
      _stopped = true;
    }

    public int CompareTo(SimpleTimer t) {
#if BRUNET_SIMULATOR
      return this._next_run.CompareTo(t._next_run);
#else
      lock(_sync) {
        return this._next_run.CompareTo(t._next_run);
      }
#endif
    }
  }

#if BRUNET_NUNIT
  [TestFixture]
  public class TimerTest {
    protected ArrayList _order = new ArrayList();
    protected Hashtable _hash = new Hashtable();
    protected object _sync = new object();
    
    public void Callback(object state) {
      lock(_sync) {
        _order.Add(state);
      }
    }

    public void PeriodCallback(object state) {
      SimpleTimer t = _hash[state] as SimpleTimer;
      int calls = 0;

      lock(_sync) {
        if(_hash.Contains(t)) {
          calls = (int) _hash[t];
        }
        _hash[t] = ++calls;
      }

      if(calls == 5) {
        t.Stop();
      }
    }

    [Test]
    public void TestDipose() {
      _order = new ArrayList();
      for(int i = 0; i < 100; i++) {
        SimpleTimer t = new SimpleTimer(Callback, i, 100 + i, 0);
        t.Start();
        if(i % 2 == 0) {
          t.Stop();
        }
      }

      Thread.Sleep(500);
      foreach(int val in _order) {
        Assert.IsTrue(val % 2 == 1, "Even value got in...");
      }
      Assert.AreEqual(_order.Count, 50, "Should be 50 in _order");
    }

    [Test]
    public void TestLargeNumberWithPeriod() {
      _hash = new Hashtable();
      _order = new ArrayList();
      for(int i = 0; i < 10000; i++) {
        SimpleTimer t = new SimpleTimer(Callback, i, 200, 0);
        t.Start();
      }

      for(int i = 0; i < 5; i++) {
        object state = new object();
        SimpleTimer t =  new SimpleTimer(PeriodCallback, state, 50, 50);
        lock(_sync) {
          _hash[state] = t;
        }
        t.Start();
      }

      Thread.Sleep(500);

      Assert.AreEqual(_order.Count, 10000, "Should be 10000");
      foreach(object o in _hash.Keys) {
        if(o is SimpleTimer) {
          Assert.AreEqual((int) _hash[o], 5, "Should be 5");
        }
      }
    }
  }
#endif
}

