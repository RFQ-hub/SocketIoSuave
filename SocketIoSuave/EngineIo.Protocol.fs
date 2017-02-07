module SocketIoSuave.EngineIo.Protocol

open SocketIoSuave

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

type Data =
| Empty
| String of string
| Binary of ByteSegment

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix )>]
module Data =
    open System.Text

    let requireBinary = function
    | Empty -> false
    | String _ -> false
    | Binary _ -> true

    let encodeToBinary = function
    | Empty -> Segment.empty
    | String s -> Encoding.UTF8.GetBytes s |> Segment.ofArray
    | Binary b -> b

    let encodeToString = function
    | Empty -> ""
    | String s -> s
    | Binary b ->
        // new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write)
        b |> Segment.toArray |> Convert.ToBase64String

    let guessEncodeToStringLength = function
    | Empty -> 0
    | String s -> s.Length
    | Binary b -> int (ceil (float b.Count / 3.) * 4.)

type OpenHandshake =
    {
        Sid: string
        Upgrades: string[]
        PingTimeout: int
        PingInterval: int
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module OpenHandshake =
    open Newtonsoft.Json
    open Newtonsoft.Json.Serialization

    let private jsonSerializerSettings =
        let result = new JsonSerializerSettings()
        result.ContractResolver <- new CamelCasePropertyNamesContractResolver()
        result

    let encode handshake =
        JsonConvert.SerializeObject(handshake, Formatting.None, jsonSerializerSettings)

    let decode s =
        JsonConvert.DeserializeObject<OpenHandshake>(s, jsonSerializerSettings)

type PacketMessage =
| Open of OpenHandshake
| Close
| Ping of Data
| Pong of Data
| Message of Data
| Upgrade
| Noop

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PacketMessage =
    open System.IO

    let getTypeId = function
    | Open _ -> 0uy
    | Close -> 1uy
    | Ping _ -> 2uy
    | Pong _ -> 3uy
    | Message _ -> 4uy
    | Upgrade -> 5uy
    | Noop -> 6uy

    let getData = function
    | Open handshake -> String (OpenHandshake.encode handshake)
    | Close -> Empty
    | Ping x -> x
    | Pong x -> x
    | Message x -> x
    | Upgrade -> Empty
    | Noop -> Empty

    let requireBinary = function
    | Open _ -> false
    | Close -> false
    | Upgrade -> false
    | Noop -> false
    | x -> x |> getData |> Data.requireBinary

    let encodeToBinary packet =
        let typeId = getTypeId packet
        let data = packet |> getData |> Data.encodeToBinary
        let result = Array.zeroCreate (data.Count + 1)
        result.[0] <- typeId
        Array.Copy(data.Array, data.Offset, result, 1, data.Count)
        result

    let encodeToString packet =
        let typeId = getTypeId packet
        let data = packet |> getData
        let header = if data |> Data.requireBinary then "b" else ""
        header + typeId.ToString() + (data |> Data.encodeToString)

    let decodeBinaryPacket (data: ByteSegment) isBinary =
        let typeId = data |> Segment.valueAt 0
        let dataBytes = data |> Segment.skip 1
        let data =
            if dataBytes.Count = 0 then
                Empty
            else if isBinary then
                Binary(dataBytes)
            else
                String(Text.Encoding.UTF8.GetString(dataBytes.Array, dataBytes.Offset, dataBytes.Count))
                
        let failIfData () = match data with | Empty -> () | _ -> failwithf "Packet of type %A doesn't support data" typeId
        match typeId with
        | 0uy ->
            match data with
            | String str ->
                let handshake = OpenHandshake.decode str
                Open handshake
            | _ -> failwithf "Unexpected content for handshake: %A" data
        | 1uy ->
            failIfData()
            Close
        | 2uy ->
            Ping(data)
        | 3uy ->
            Pong(data)
        | 4uy ->
            Message(data)
        | 5uy ->
            failIfData()
            Upgrade
        | 6uy ->
            failIfData()
            Noop
        | _ -> failwithf "Unknown packet type: %A" typeId
            

type Payload =
| Payload of PacketMessage list

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Payload =
    open System.IO
    open System.Text

    let getMessages = function | Payload m -> m

    let encodeToBinary payload =
        let messages = payload |> getMessages
        let stream = new MemoryStream()
        for message in messages do
            let requireBinary = message |> PacketMessage.requireBinary
            stream.WriteByte(if requireBinary then 1uy else 0uy)
            let binData =
                if requireBinary then
                    message |> PacketMessage.encodeToBinary
                else
                    Encoding.UTF8.GetBytes(message |> PacketMessage.encodeToString)
            let sizeString = binData.Length.ToString()
            for chr in sizeString do
                stream.WriteByte(Byte.Parse(string chr))
            stream.WriteByte(255uy)
            stream.Write(binData, 0, binData.Length)
        stream.ToArray()

    let encodeToString payload =
        let messages = payload |> getMessages
        if messages.Length = 0 then
            "0:"
        else
            let sizeGuess = messages |> List.fold (fun acc m -> acc + (m |> PacketMessage.getData |> Data.guessEncodeToStringLength) + 6) 0
            let builder = StringBuilder(sizeGuess)
            for message in messages do
                let messageStr = message |> PacketMessage.encodeToString
                builder.Append(messageStr.Length) |> ignore
                builder.Append(':') |> ignore
                builder.Append(messageStr) |> ignore
            
            builder.ToString()

    let decodeBinaryPayload (data: ByteSegment) =
        let mutable currentPos = data.Offset
        while currentPos < data.Offset + data.Count do
            let isBinary = data.Array.[currentPos] = 1uy
            currentPos <- currentPos + 1
            