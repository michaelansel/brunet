/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California
Copyright (C) 2005,2006  P. Oscar Boykin <boykin@pobox.com>, University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

//#define LINK_DEBUG

using System;
using System.Collections;


namespace Brunet 
{

  /**
   * Is a state machine to handle the link protocol for
   * one particular attempt, on one particular Edge, which
   * was created using one TransportAddress
   */
  public class LinkProtocolState : TaskWorker, ILinkLocker, IDataHandler {
   
    /**
     * When this state machine reaches the end, it fires this event
     */
    protected bool _is_finished;
    public override bool IsFinished {
      get {
        lock( _sync ) { return _is_finished; }
      }
    }
    protected MemBlock _last_r_packet;
    public MemBlock LastRPacket {
      get { return _last_r_packet; }
    }
    protected long _last_packet_datetime;
    
    protected ConnectionMessage _last_r_mes;
    protected bool _sent_status;
    
    protected ConnectionMessageParser _cmp;
    protected Connection _con;
    /**
     * If this state machine creates a Connection, this is it.
     * Otherwise its null
     */
    public Connection Connection { get { lock( _sync) { return _con; } } }
    protected ConnectionMessage _last_s_mes;
    protected readonly Linker _linker;
    /**
     * The Linker that created this LinkProtocolState
     */
    public Linker Linker { get { return _linker; } }
    protected Packet _last_s_packet;
    /**
     * The Packet we last sent
     */
    public Packet LastSPacket { get { lock( _sync ) { return _last_s_packet; } } }

    volatile protected Exception _x;
    /**
     * If we catch some exception, we store it here, and call Finish
     */
    public Exception CaughtException { get { return _x; } }

    protected ErrorMessage _em;
    /**
     * If we receive an ErrorMessage from the other node, this is it.
     */
    public ErrorMessage EM { get { return _em; } }

    protected readonly Node _node;
    protected readonly string _contype;
    protected Address _target_lock;
    protected int _id;
    protected object _sync;
    protected Edge _e;
    protected readonly TransportAddress _ta;
    public TransportAddress TA { get { return _ta; } }

    //This is an object that represents the task
    //we are working on.
    public override object Task {
      get { return _ta; }
    }
  
    /**
     * How many time outs are allowed before assuming failure
     */
    protected static readonly int _MAX_TIMEOUTS = 3;
    protected int _timeouts;
    /**
     * The timeout is adaptive.  It goes up
     * by a factor of _TIMEOUT_FACTOR
     * after each timeout.  It starts at DEFAULT_TIMEOUT second.
     * Then _TIMEOUT_FACTOR * DEFAULT_TIMEOUT ...
     */
    protected static readonly int _TIMEOUT_FACTOR = 2;
    protected static readonly int DEFAULT_TIMEOUT = 1000;
    protected int _ms_timeout = DEFAULT_TIMEOUT;
    
    /**
     * Holds a long representing the current timeout, which
     * is a function of how many previous timeouts there have been
     * This is measured in 100 ns ticks (What DateTime uses)
     */
    protected long _timeout;

    /*
     * The enumerator holds the state of the current attempt
     */
    protected IEnumerator _link_enumerator = null;

    public enum Result {
      ///Everything succeeded and we created a Connection
      Success,
      ///This TransportAddress or Edge did not work.
      MoveToNextTA,
      ///This TransportAddress may/should work if we try again
      RetryThisTA,
      ///Received some ErrorMessage from the other node (other than InProgress)
      ProtocolError,
      ///There was some Exception
      Exception,
      ///No result yet
      None
    }

    protected LinkProtocolState.Result _result;
    /**
     * When this object is finished, this tells the Linker
     * what to do next
     */
    public LinkProtocolState.Result MyResult { get { return _result; } }

    public LinkProtocolState(Linker l, TransportAddress ta, Edge e) {
      _linker = l;
      _node = l.LocalNode;
      _contype = l.ConType;
      _sync = new object();
      _id = 1;
      _target_lock = null;
      _sent_status = false;
      _timeout = TimeUtils.MsToNsTicks( _ms_timeout ); //_timeout is in 100 ns ticks
      _cmp = new ConnectionMessageParser();
      _ta = ta;
      _is_finished = false;
      //Setup the edge:
      _e = e;
      _e.Subscribe(this, _e);
      _e.CloseEvent += new EventHandler(this.CloseHandler);
    }

    //Make sure we are unlocked.
    ~LinkProtocolState() {
      lock( _sync ) {
        if( _target_lock != null ) {
          Console.Error.WriteLine("Lock released by destructor");
          Unlock();
        }
      }
    }

    ///We should allow it as long as it is not another LinkProtocolState:
    public bool AllowLockTransfer(Address a, string contype, ILinkLocker l)
    {
	bool allow = false;
	lock( _sync ) {
          if( l is Linker ) {
            //We will allow it if we are done:
            if( _is_finished ) {
              allow = true;
              _target_lock = null;
            }
          }
          else if ( false == (l is LinkProtocolState) ) {
            /**
  	   * We only allow a lock transfer in the following case:
             * 0) We have not sent the StatusRequest yet.
  	   * 1) We are not transfering to another LinkProtocolState
  	   * 2) The lock matches the lock we hold
  	   * 3) The address we are locking is greater than our own address
  	   */
            if( (!_sent_status )
                  && a.Equals( _target_lock )
  	        && contype == _contype 
  		&& ( a.CompareTo( _node.Address ) > 0) ) {
                _target_lock = null; 
                allow = true;
  	    }
  	  }
          if( allow ) {
            _target_lock = null;
          }
	}
	return allow;
    }
    /**
     * When this state machine reaches an end point, it calls this method,
     * which fires the FinishEvent
     */
    protected void Finish(Result res) {
      /*
       * No matter what, we are done here:
       */
      Edge to_close = null;
      bool close_gracefully = false;
      lock( _sync ) {
        if( _is_finished ) { throw new Exception("Finished called twice!"); }
        _is_finished = true;
        _result = res;
        _node.HeartBeatEvent -= new EventHandler(this.PacketResendCallback);
        _e.Unsubscribe(this);
        _e.CloseEvent -= new EventHandler(this.CloseHandler);
        if( this.Connection == null ) {
          to_close = _e;
          _e = null;
          close_gracefully = ( LastRPacket != null);
        }
      }
      /*
       * In some cases, we close the edge:
       */
      if( to_close != null ) {
        if( close_gracefully ) {
        /*
         * We close the edge if we did not get a Connection AND we received
         * some response from this edge
         */
          CloseMessage close = new CloseMessage();
          close.Dir = ConnectionMessage.Direction.Request;
          _node.GracefullyClose(to_close, close);
        }
        else {
          /*
           * We never heard from the other side, so we will assume that further
           * packets will only waste bandwidth
           */
          to_close.Close();
        }
      }
      else {
        //We got a connection, don't close it!
      }
      FireFinished();
      /**
       * We have to make sure the lock is eventually released:
       */
      this.Unlock();
    }
    /**
     * Set the _target member variable and check for sanity
     * We only set the target if we can get a lock on the address
     * We can call this method more than once as long as we always
     * call it with the same value for target
     * If target is null we just return
     * 
     * @param target the value to set the target to.
     * 
     * @throws LinkException if the target is already * set to a different address
     * @throws System.InvalidOperationException if we cannot get the lock
     */
    protected void SetTarget(Address target)
    {
      if ( target == null )
        return;
 
     lock(_sync) {
      ConnectionTable tab = _node.ConnectionTable;
      if( _target_lock != null ) {
        //This is the case where _target_lock has been set once
        if( ! target.Equals( _target_lock ) ) {
          throw new LinkException("Target lock already set to a different address");
        }
      }
      else if( target.Equals( _node.Address ) )
        throw new LinkException("cannot connect to self");
      else {
        lock( tab.SyncRoot ) {
          if( tab.Contains( Connection.StringToMainType( _contype ), target) ) {
            throw new LinkException("already connected");
          }
          //Lock throws an InvalidOperationException if it cannot get the lock
          tab.Lock( target, _contype, this );
          _target_lock = target;
        }
      }
     }
    }

    /**
     * Unlock any lock which is held by this state
     */
    public void Unlock() {
      lock( _sync ) {
        ConnectionTable tab = _node.ConnectionTable;
        tab.Unlock( _target_lock, _contype, this );
        _target_lock = null;
      }
    }
    
    protected IEnumerator GetEnumerator() {
      //Here we make and yield the LinkMessage request.
      NodeInfo my_info = new NodeInfo( _node.Address, _e.LocalTA );
      NodeInfo remote_info = new NodeInfo( _linker.Target, _e.RemoteTA );
      System.Collections.Specialized.StringDictionary attrs
          = new System.Collections.Specialized.StringDictionary();
      attrs["type"] = _contype;
      attrs["realm"] = _node.Realm;
      _last_s_mes = new LinkMessage( attrs, my_info, remote_info );
      _last_s_mes.Dir = ConnectionMessage.Direction.Request;
      _last_s_mes.Id = _id++;
      _last_s_packet = _last_s_mes.ToPacket();
      _last_packet_datetime = TimeUtils.NoisyNowTicks;
#if LINK_DEBUG
      Console.Error.WriteLine("LinkState: To send link request: {0}; Length: {1} at: {2}", _last_s_mes, _last_s_packet.Length, DateTime.Now);
#endif
      yield return _last_s_packet;
      //We should now have the response:
      /**
       * When we receive a LinkMessage response, we know
       * the other party is willing to link with us.
       * To acknowledge that we can complete the link,
       * we send them a StatusMessage request.
       *
       * The other node must not consider the Edge connected
       * until the StatusMessage request is received.
       */
      //Build the neighbor list:
      LinkMessage lm = (LinkMessage)_last_r_mes;
      /*
       * So, we must have our link message now:
	 * Make sure the link message is Kosher.
       * This are critical errors.  This Link fails if these occur
	 */
      if( lm.ConTypeString != _contype ) {
        throw new LinkException("Link type mismatch: "
                                + _contype + " != " + lm.ConTypeString );
      }
      if( lm.Attributes["realm"] != _node.Realm ) {
        throw new LinkException("Realm mismatch: " +
                                _node.Realm + " != " + lm.Attributes["realm"] );
      }
      if( (_linker.Target != null) && (!lm.Local.Address.Equals( _linker.Target )) ) {
        /*
         * This is super goofy.  Somehow we got a response from some node
         * we didn't mean to connect to.
         * This can happen in some cases with NATs since nodes behind NATs are
         * guessing which ports are correct, their guess may be incorrect, and
         * the NAT may send the packet to a different node.
         * In this case, we have a critical error, this TA is not correct, we
         * must move on to the next TA.
         */
        throw new LinkException(String.Format("Target mismatch: {0} != {1}",
                                              _linker.Target, lm.Local.Address), true, null );
      }
      //Make sure we have the lock on this address, this could 
      //throw an exception halting this link attempt.
      lock( _sync ) {
	  SetTarget( lm.Local.Address );
        //At this point, we cannot be pre-empted.
        _sent_status = true;
      }
	
      _last_s_mes = _node.GetStatus(lm.ConTypeString, lm.Local.Address);
      _last_s_mes.Id = _id++;
      _last_s_mes.Dir = ConnectionMessage.Direction.Request;
      _last_s_packet = _last_s_mes.ToPacket();
#if LINK_DEBUG
      Console.Error.WriteLine("LinkState: To send status request: {0}; Length: {1} at: {2}", _last_s_mes, _last_s_packet.Length, DateTime.Now);
#endif
      yield return _last_s_packet;
      StatusMessage sm = (StatusMessage)_last_r_mes;
      Connection con = new Connection(_e, lm.Local.Address, lm.ConTypeString,
				        sm, lm);
#if LINK_DEBUG
      Console.Error.WriteLine("LinkState: New connection added. ");
#endif
      //Return the connection, now we are done!
      yield return con;
    }
    protected Result HandleError(ErrorMessage em) {
      Result result = Result.None;
        //We got an error
	if( em.Ec == ErrorMessage.ErrorCode.InProgress ) {
#if LINK_DEBUG
        Console.Error.WriteLine("Linker ({0}) InProgress: from: {1}", _linker.Lid, edge);
#endif
          result = Result.RetryThisTA;
        }
        else if ( em.Ec == ErrorMessage.ErrorCode.AlreadyConnected ) {
          /*
           * The other side thinks we are already connected.  This is
           * odd, let's see if we agree
           */
          Address target = _linker.Target;
          ConnectionTable tab = _node.ConnectionTable;
          if( target == null ) {
            //This can happen with leaf connections.  In this case, we
            //should move on to another TA.
            result = Result.MoveToNextTA;
          }
          else if( tab.Contains( Connection.StringToMainType( _contype ), target) ) {
            //This shouldn't happen
            result = Result.ProtocolError;
            Console.Error.WriteLine("LPS: already connected: {0}, {1}", _contype, target);
          }
          else {
            //The other guy thinks we are connected, but we disagree,
            //let's retry.  This can happen if we get disconnected
            //and reconnect, but the other node hasn't realized we
            //are disconnected.
            result = Result.RetryThisTA;
          }
        }
        else if ( em.Ec == ErrorMessage.ErrorCode.TargetMismatch ) {
          /*
           * This could happen in some NAT cases, or perhaps due to
           * some other as of yet undiagnosed bug.
           *
           * Move to the next TA since this TA definitely connects to
           * the wrong guy.
           */
          Console.Error.WriteLine("LPS: from {0} target mismatch: {1}", _e, _em);
          result = Result.MoveToNextTA;
        }
        else {
          //We failed.
          result = Result.ProtocolError;
        }
        return result;
    }
    /**
     * When we get packets from the Edge, this is how we handle them
     */
    public void HandleData(MemBlock p, ISender edge, object state)
    {
      Packet to_send = null;
      bool finish = false;
      Result result = Result.None;
#if LINK_DEBUG
      Console.Error.WriteLine("From: {0}\nPacket: {1}\n\n",edge, p);
#endif
      ErrorMessage em = null;
      lock( _sync ) {
        if( _is_finished ) { return; }
        try {
          em = SetLastRPacket(p);
          _em = em;
          _timeouts = 0;
          //If we get here, the packet must have been what we were expecting,
          //or an ErrorMessage
          if( em != null ) {
            result = HandleError(em);
            finish = true;
          }
        }
        catch(Exception) {
        /*
         * SetLastRPacket can throw an exception on expected packets
         * for now, we just ignore them and resend the most recently
         * sent packet:
         */
	  to_send = LastSPacket;
        }
        if( !finish ) {
          try {
            //Advance one step in the protocol
            if( _link_enumerator.MoveNext() ) {
              object o = _link_enumerator.Current;
              if( o is Packet ) {
                to_send = (Packet)o;
              }
              else if (o is Connection) {
                _con = (Connection)o;
                //We have created our connection, Success!
                result = Result.Success;
                finish = true;
              }
            }
            else {
               //We should never get here
            }
          }
          catch(InvalidOperationException x) {
            //This is thrown when ConnectionTable cannot lock.  Lets try again:
  #if LINK_DEBUG
          Console.Error.WriteLine("Linker ({0}): Could not lock in HandlePacket", _linker.Lid);
  #endif
            _x = x;
            result = Result.RetryThisTA;
            finish = true;
          }
          catch(LinkException x) {
            _x = x;
            if( x.IsCritical ) {
              result = Result.MoveToNextTA;
            }
            else {
              result = Result.RetryThisTA;
            }
            finish = true;
          }
          catch(Exception x) {
            //The protocol was not followed correctly by the other node, fail
            _x = x;
            result = Result.RetryThisTA;
            finish = true;
          }
        }
      }
      if( null != to_send) {
        edge.Send( to_send );
      }
      if( finish ) {
        Finish(result);
      }
    }

    public override void Start() {
      Edge e = null;
      Packet p = null;
      lock( _sync ) {
        _link_enumerator = GetEnumerator();
        _link_enumerator.MoveNext(); //Move the protocol forward:
        p = (Packet)_link_enumerator.Current;
        e = _e;
      }
      e.Send(p);
      //Register the call back:
      _node.HeartBeatEvent += this.PacketResendCallback;
    }
  
    /**
     * This only gets called if the Edge closes unexpectedly.  If the
     * Edge closes normally, we would have already stopped listening
     * for CloseEvents.  If the Edge closes unexpectedly, we MoveToNextTA
     * to signal that this is not a good candidate to retry.
     */
    protected void CloseHandler(object sender, EventArgs args) {
      Finish(Result.MoveToNextTA);
    }

    protected void PacketResendCallback(object node, EventArgs args)
    {
      bool finish = false;
      Result result = Result.None;
      Packet to_send = null;
      Edge e = null;
      lock( _sync ) {
        try {
	  long now = TimeUtils.NoisyNowTicks;
          if( (! _is_finished) &&
              (now - _last_packet_datetime > _timeout ) ) {
            /*
             * It is time to check to see if we should resend, or move on
             */

            if (_timeouts < _MAX_TIMEOUTS) {
#if LINK_DEBUG
            Console.Error.WriteLine("Linker ({0}) resending packet; attempt # {1}; length: {2}", _linker.Lid, _timeouts,                              LastSPacket.Length);
#endif
              to_send = LastSPacket;
              e = _e;
              _last_packet_datetime = now;
	      //Increase the timeout by a factor of 4
	      _ms_timeout = _TIMEOUT_FACTOR * _ms_timeout;
              //_timeout is in 100 ns ticks
              _timeout = TimeUtils.MsToNsTicks( _ms_timeout );
              _timeouts++;
            }
            else if( _timeouts >= _MAX_TIMEOUTS ) {
              //This edge is not working, we need to restart on a new edge.
#if LINK_DEBUG
              Console.Error.WriteLine("Linker ({0}) giving up the TA, moving on to next", _linker.Lid);
#endif
              result = Result.MoveToNextTA;
              finish = true;

            }
          }
        }
        catch(Exception ex) {
          _x = ex;
          result = Result.MoveToNextTA;
          finish = true;
        }
      }
      if( to_send != null ) { 
        e.Send( to_send );
      }
      if( finish ) {
        Finish(result);
      }
    }
    
    /**
     * Set the last packet and check for error conditions.
     * If the packet contains an ErrorMessage, that ErrorMessage
     * is returned, else null
     */
    protected ErrorMessage SetLastRPacket(MemBlock p) {
        MemBlock payload = null;
        PType pt = PType.Parse(p, out payload);
        if( !pt.Equals( PType.Protocol.Linking ) ) {
          throw new LinkException(
                    String.Format("Not a ConnectionProtocol Packet: {0}", pt) );
        }
        ConnectionMessage cm = _cmp.Parse( payload.ToMemoryStream() );
        //Check to see that the id matches and it is a request:
        if( cm.Id != _last_s_mes.Id ) {
          //There is an ID mismatch
          throw new LinkException("ID number mismatch");
        }
        if( cm.Dir != ConnectionMessage.Direction.Response ) {
          //This is not a response, as we expect.
          throw new LinkException("received a message that is not a Response");
        }
        if( cm is ErrorMessage ) {
          return (ErrorMessage)cm;
        }
        //Everything else looks good, lets make sure it is the right type:
        if( ! cm.GetType().Equals( _last_s_mes.GetType() ) ) {
          //This is not the same type 
          throw new LinkException("ConnectionMessage type mismatch");
        }
        //They must be sane, or the above would have thrown exceptions
        _last_r_packet = p;
        _last_r_mes = cm;
        _last_packet_datetime = TimeUtils.NoisyNowTicks;
	return null;
    }
  }
}
