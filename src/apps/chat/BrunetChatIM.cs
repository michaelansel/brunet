namespace Brunet 
{
using System;
using GtkSharp;
using Gtk;
using Gdk;
using Glade;
using System.Configuration;
using System.Text;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Security.Cryptography;
public class BrunetChatIM 
{
  /** The chat window widget. There is precisely one ChatIM for each
   * conversation.
   */
  [Glade.Widget]      
  private Gtk.Window windowBrunetChatIM; 
  
  /** The message send widget. This widget is the default action in the ChatIM
   * window...that is <enter> triggers the same handler as a click. 
   */
  [Glade.Widget]      
  private Gtk.Button buttonSend;

  /** The conversation is displayed here.
   */
  [Glade.Widget]      
  private Gtk.TextView textviewDisplay;

  /** The outgoing text is entered here.
   */
  [Glade.Widget]      
  private Gtk.TextView textviewInput;

  /** The recipient of messages initiated in this ChatIM is displayed here.
   */
  [Glade.Widget]      
  private Gtk.TextView textviewRecipient;

  /// Buffers for the above TextView widgets.
  ///
 
  private Gtk.TextBuffer _text_buf_display;
  private Gtk.TextBuffer _text_buf_input;
  private Gtk.TextBuffer _text_buf_recipient;
 
  /** The BrunetNode for this chat program instance. This quantity is a
   * reference to the node in BrunetChatMain.cs.
   */
  private StructuredNode _brunet_node;

  /** A reference to the main chat window application object.
   */
  private BrunetChatMain _brunet_chat_main;
  
  /** The buddy who will recieve messages.
   */
  private Buddy _recipient_buddy;
  
  /** The Brunet address of the recipient.
   */
  private AHAddress _to_address;

  /** The Brunet address of the sender.
   */
  private AHAddress _from_address;
  
  /** This string is prepended before each outgoing message.
   */
  private string _sender_alias;
  
  public AHAddress ToAddress
  {
    get
    {
      return _to_address;
    }
  }
  
  /** ChatIM constructor. 
   *  @param core the main application
   *  @param r_add the recipient address
   */
  public BrunetChatIM(BrunetChatMain core,AHAddress r_add)
  {
    _brunet_chat_main = core;
    _brunet_node = _brunet_chat_main.BrunetNode;
    _from_address = (AHAddress)_brunet_node.Address; 
    _to_address = r_add;
    
    string fname = "BrunetChat.glade";
    string root = "windowBrunetChatIM";

    Glade.XML gxml = new Glade.XML (fname, root, null);
    //Glade.XML gxml = new Glade.XML (null,fname, root, null);
    gxml.Autoconnect(this);
    
    _text_buf_display = textviewDisplay.Buffer;
    _text_buf_input = textviewInput.Buffer;
    _text_buf_recipient = textviewRecipient.Buffer;
    _recipient_buddy = (Buddy)_brunet_chat_main.BuddyHash[_to_address]; 
    _text_buf_recipient.Text = _recipient_buddy.Alias;
    _sender_alias = (string)_brunet_chat_main.CurrentUser.Alias;
  }

  /** Button click handler.  This sends input text to the node for delivery
   * and also echoes the text into the display window.
   */
  public void  OnButtonSendClicked(object obj, EventArgs e) 
  {
    if (null != obj){
      if (_text_buf_input != null){
        if (_text_buf_input.CharCount > 0 ){
          SendText(_text_buf_input.Text);
          _text_buf_display.Text += "<"+ _sender_alias +"> ";
          _text_buf_display.Text += _text_buf_input.Text;
          _text_buf_display.Text += "\n";
          _text_buf_display.MoveMark(
              _text_buf_display.InsertMark, 
              _text_buf_display.EndIter);
          textviewDisplay.ScrollToMark(
              _text_buf_display.InsertMark, 
              0.4,
              true, 
              0.0, 
              1.0);
          _text_buf_input.Clear();
        }  
      }
    }
    else
    {
      Console.WriteLine("Gtk error null reference");
      throw new NullReferenceException();
    }
  }

  /** Packetize the text as UTF8 and send it.  Eventually we will want to use
   * Jabber or some other standard meesage format.
   * @param sendtext This string will be packetized and sent to the recipient.
   */
  protected void SendText(string sendtext)
  {
    byte[] payload = Encoding.UTF8.GetBytes(sendtext);
    short hops =0;
    short ttl =137;
    AHPacket mp = new AHPacket(
        hops,
        ttl,
        _from_address,
        _to_address,
        AHPacket.Protocol.Chat,
        payload);	  
    _brunet_node.Send(mp);
  }
 
  /** This is called when new text arrives from the recipient.
   * Text is inserted into the display, the display is scrolled if needed and
   * the message is written to the console for debugging.
   */
  public void DeliverMessage(object ob)
  {
    if (null != ob){
      string a_msg = (string)ob;
      _text_buf_display.Insert(
          _text_buf_display.EndIter,
          "<"+_recipient_buddy.Alias+"> " );
      
      Console.WriteLine(a_msg ); 
      
      _text_buf_display.Insert(_text_buf_display.EndIter,a_msg);
      _text_buf_display.Insert(
          _text_buf_display.EndIter,
          System.Environment.NewLine);
      
      _text_buf_display.MoveMark(
          _text_buf_display.InsertMark, 
          _text_buf_display.EndIter);
      textviewDisplay.ScrollToMark(
          _text_buf_display.InsertMark, 
          0.4,
          true, 
          0.0, 
          1.0);
    }
    else
      Console.WriteLine("Message is NULL" ); 
  }

  public void OnWindowDeleteEvent (object o, DeleteEventArgs args) 
	{
    _brunet_chat_main.MessageHandler.MessageSinks.Remove(_to_address);
		args.RetVal = true;
    windowBrunetChatIM.Destroy();
	}
  
}

}
