namespace SocketIoSuave.EngineIo.Protocol

(*

https://github.com/socketio/engine.io
https://github.com/socketio/engine.io-protocol
https://github.com/socketio/engine.io-parser/blob/master/lib/browser.js
https://github.com/Quobject/EngineIoClientDotNet/blob/master/Src/EngineIoClientDotNet.mono/Parser/Packet.cs
https://socketio4net.codeplex.com/SourceControl/latest#SocketIO/IEndPointClient.cs
https://github.com/ocharles/engine.io
*)

open System

type ByteSegment = ArraySegment<byte>

/// Content of a engine.io packet, text, binary data or empty
type PacketContent =
    /// No packet content
    | Empty
    /// Text packet content (Will be transfered as UTF-8)
    | TextPacket of string
    /// Binary packet content (Transfered either directly or as Base64 depending on transport)
    | BinaryPacket of ByteSegment

type OpenHandshake =
    {
        Sid: string
        Upgrades: string[]
        PingTimeout: int
        PingInterval: int
    }

type PacketMessage =
    | Open of OpenHandshake
    | Close
    | Ping of PacketContent
    | Pong of PacketContent
    | Message of PacketContent
    | Upgrade
    | Noop

type Payload =
    | Payload of PacketMessage list

type Transport =
    | Polling
    | Websocket
