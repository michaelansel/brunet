/** The class implements the unreliable transport
    provided by Brunet; assuming that the node is connected to the network
 **/
using Brunet;
using System.Net;
using System.Collections;
using System;

namespace Ipop {
  public class BrunetTransport {
    Node brunetNode;

    public BrunetTransport(Ethernet ether, string brunet_namespace, 
      NodeMapping node, EdgeListener []EdgeListeners, string [] DevicesToBind,
      ArrayList RemoteTAs, bool debug) {
      //local node
      AHAddress us = new AHAddress(IPOP_Common.GetHash(node.ip));
      Console.WriteLine("Generated address: {0}", us);
      brunetNode = new StructuredNode(us, brunet_namespace);

      //Where do we listen:
      IPAddress[] tas = Routines.GetIPTAs(DevicesToBind);

      foreach(EdgeListener item in EdgeListeners) {
        int port = 0;
        if(item.port_high != null && item.port_low != null && item.port == null) {
          int port_high = Int32.Parse(item.port_high);
          int port_low = Int32.Parse(item.port_low);
          Random random = new Random();
          port = (random.Next() % (port_high - port_low)) + port_low;
          }
        else
            port = Int32.Parse(item.port);
        if (item.type =="tcp") { 
            brunetNode.AddEdgeListener(new TcpEdgeListener(port, tas));
        }
        else if (item.type == "udp") {
            brunetNode.AddEdgeListener(new UdpEdgeListener(port , tas));
        }
        else if (item.type == "udp-as") {
            brunetNode.AddEdgeListener(new ASUdpEdgeListener(port, tas));
        }
        else {
          throw new Exception("Unrecognized transport: " + item.type);
        }
      }

      //Here is where we connect to some well-known Brunet endpoints
      brunetNode.RemoteTAs = RemoteTAs;

      //now try sending some messages out 
      //subscribe to the IP protocol packet

      IPPacketHandler ip_handler = new IPPacketHandler(ether, debug, node);
      brunetNode.Subscribe(AHPacket.Protocol.IP, ip_handler);

      brunetNode.Connect();
      System.Console.WriteLine("Called Connect");
    }

    //method to send a packet out on the network
    public void SendPacket(AHAddress target, byte[] packet) {
      AHPacket p = new AHPacket(0, 30,   brunetNode.Address,
        target, AHPacket.AHOptions.Exact,
        AHPacket.Protocol.IP, packet);
      brunetNode.Send(p);
    }

    public void Disconnect() {
      brunetNode.Disconnect();
    }
  }
}
