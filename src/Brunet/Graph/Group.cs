/*
This program is part of Brunet, a library for autonomic overlay networks.
Copyright (C) 2010 David Wolinsky davidiw@ufl.edu, Unversity of Florida

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

using NDesk.Options;
using System;
using System.Collections.Generic;

using Brunet.Symphony;
using Brunet.Util;

namespace Brunet.Graph {
  public class GroupParameters : Parameters {
    public int GroupSize { get { return _group_size; } }
    protected int _group_size = 0;

    public GroupParameters() :
      base("GroupGraph", "GroupGraph - Brunet Network Modeler for Groups.")
    {
      _options.Add("g|group_size=", "Number of peers in the group",
          v => _group_size = Int32.Parse(v));
    }

    public override void Parse(string[] args)
    {
      base.Parse(args);

      if(_group_size <= 0) {
        _error_message = "GroupSize is less than or equal to 0.";
      }
    }
  }

  public class GroupGraph : Graph {
    protected List<GraphNode> _group_members;
    protected MemBlock _group_identifier;

    public GroupGraph(int count, int near, int shortcuts, int random_seed,
        List<List<int>> dataset, int group_count) :
      base(count, near, shortcuts, random_seed, dataset)
    {
      if(group_count > count || group_count < 0) {
        throw new Exception("Invalid group count: " + group_count);
      }

      _group_members = new List<GraphNode>();

      for(int i = 0; i < group_count; i++) {
        int index = _rand.Next(0, count);
        AHAddress addr = _addrs[index];
        _group_members.Add(_addr_to_node[addr]);
      }

      _group_identifier = GenerateAddress().ToMemBlock();
    }

    public void GroupBroadcastByUnicast()
    {
      int group_count = _group_members.Count;
      int total = (group_count - 1) * (group_count - 1);
      List<int> hops = new List<int>(total);
      List<int> delays = new List<int>(group_count - 1);

      foreach(GraphNode src in _group_members) {
        int delay = 0;

        foreach(GraphNode dst in _group_members) {
          if(src == dst) {
            continue;
          }
          var results = SendPacket(src.Address, dst.Address);
          if(results.Count == 0) {
            throw new Exception("SendPacket failed!");
          }

          hops.Add(results[0].Hops);
          delay = System.Math.Max(results[0].Delay, delay);
        }
        delays.Add(delay);
      }

      Console.WriteLine("GroupBroadcastByUnicast results:");
      double average = Average(hops);
      Console.WriteLine("\tHops: Average: {0}, Stdev: {1}", average,
          StandardDeviation(hops, average));
      average = Average(delays);
      Console.WriteLine("\tDelay: Average: {0}, Stdev: {1}", average,
          StandardDeviation(delays, average));
    }

    public void DhtGroupQuery()
    {
      int total = _group_members.Count;
      long total_delay = 0;
      List<int> delays = new List<int>(total);

      foreach(GraphNode src in _group_members) {
        int delay = DhtQuery(src.Address, _group_identifier);
        total_delay += delay;
        delays.Add(delay);
      }

      Console.WriteLine("DhtGroupQuery results:");
      double average = Average(delays);
      Console.WriteLine("\tDelay: Total: {0}, Average: {1}, Stdev: {2}",
          total_delay, average, StandardDeviation(delays, average));
    }

    public static void Main(string[] args)
    {
      GroupParameters p = new GroupParameters();
      p.Parse(args);

      if(p.Help) {
        p.ShowHelp();
        return;
      }
      if(p.ErrorMessage != string.Empty) {
        Console.WriteLine(p.ErrorMessage);
        p.ShowHelp();
        return;
      }

      Console.WriteLine("Creating a graph with base size: {0}, near " + 
          "connections: {1}, shortcuts {2}, group size: {3}",
          p.Size, p.Near, p.Shortcuts, p.GroupSize);

      GroupGraph graph = new GroupGraph(p.Size, p.Near, p.Shortcuts, p.Seed,
          p.LatencyMap, p.GroupSize);
      Console.WriteLine("Done populating graph...");

      graph.DhtGroupQuery();
//      graph.GroupBroadcastByUnicast();
//      graph.BroadcastAverage();

      if(p.Outfile != string.Empty) {
        Console.WriteLine("Saving dot file to: " + p.Outfile);
        graph.WriteGraphFile(p.Outfile);
      }
    }
  }
}
