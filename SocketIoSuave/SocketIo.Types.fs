namespace SocketIoSuave.SocketIo

open Newtonsoft.Json.Linq

/// Type of a socket.io packet
type PacketType =
    /// Connection request
    /// Can either be accepted (The server send back Connect) or refused (Error)
    | Connect
    
    /// A disconnection request
    | Disconnect
    
    /// A message sent from one side to the other
    | Event
    
    /// Acknowledge a previous Event that was sent with EventId set
    | Ack
    
    /// Signal that an error hapenned
    | Error

/// A socket.io packet
type Packet =
    {
        /// Type of the packet being transmited
        Type: PacketType
        
        /// Destination namespace, default is "/"
        Namespace: string
        
        /// Id of the event, if provided an ack should be sent to respond to this packet
        EventId: int option

        /// JSon data associated with the packet, can contain simple json types or byte arrays.
        Data: JToken list
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Packet =
    let ofType t = { Type = t; Namespace = "/"; EventId = None; Data = [] }