namespace SocketIoSuave.EngineIo

(*

https://github.com/socketio/engine.io
https://github.com/socketio/engine.io-protocol
https://github.com/socketio/engine.io-parser/blob/master/lib/browser.js
https://github.com/Quobject/EngineIoClientDotNet/blob/master/Src/EngineIoClientDotNet.mono/Parser/Packet.cs
https://socketio4net.codeplex.com/SourceControl/latest#SocketIO/IEndPointClient.cs
https://github.com/ocharles/engine.io
*)

open System

/// Part of a byte[]
type ByteSegment = ArraySegment<byte>

/// Content of a engine.io packet, text, binary data or empty
type PacketContent =
    /// No packet content
    | Empty
    /// Text packet content (Will be transfered as UTF-8)
    | TextPacket of string
    /// Binary packet content (Transfered either directly or as Base64 depending on transport)
    | BinaryPacket of ByteSegment

/// Initial message sent when a new transport is opened
type OpenHandshake =
    {
        Sid: string
        Upgrades: string[]
        PingTimeout: int
        PingInterval: int
    }

/// Messages that can be transmited on an Engine.IO connection
type PacketMessage =
    /// Sent from the server when a new transport is opened
    | Open of OpenHandshake

    /// Request the close of this transport but does not shutdown the connection itself
    | Close

    /// Sent by the client. Server should answer with a pong packet containing the same data
    | Ping of PacketContent

    /// Sent by the server to respond to ping packets
    | Pong of PacketContent

    /// Actual message
    | Message of PacketContent

    /// Request an upgrade to this protocol
    | Upgrade
    
    /// Does nothing.
    | Noop

/// A list of packets
type Payload =
    | Payload of PacketMessage list

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
[<RequireQualifiedAccess>]
module Payload =
    let getMessages = function | Payload m -> m

/// The transport being used to transmit Engine.IO packets
type Transport =
    /// Communication via polling, either XHR for recent browsers or JSONP for old ones
    | Polling

    /// Communication via websocket
    | Websocket
