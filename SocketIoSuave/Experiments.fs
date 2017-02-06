module Experiments

(*

https://github.com/socketio/engine.io
https://github.com/socketio/engine.io-protocol
https://github.com/socketio/engine.io-parser/blob/master/lib/browser.js
https://github.com/Quobject/EngineIoClientDotNet/blob/master/Src/EngineIoClientDotNet.mono/Parser/Packet.cs
https://socketio4net.codeplex.com/SourceControl/latest#SocketIO/IEndPointClient.cs
https://github.com/ocharles/engine.io
*)

module Segment =
    open System

    let ofArray (arr: 't[]) = new ArraySegment<'t>(arr)

    let empty = Array.empty |> ofArray

    let take count (segment: ArraySegment<'t>) =
        new ArraySegment<'t>(segment.Array, segment.Offset, min count segment.Count)

    let totalSize (segments: ArraySegment<'t> seq) =
        segments |> Seq.sumBy (fun s -> s.Count)

    let concat (segments: ArraySegment<'t> seq) =
        let count = totalSize segments
        let result = Array.create<'t> count Unchecked.defaultof<'t>
        let mutable i = 0L
        for segment in segments do
            Array.Copy(segment.Array, int64 segment.Offset, result, i, int64 segment.Count)
            i <- i + (int64 segment.Count)
        result

    let toArray (segment: ArraySegment<'t>) =
        if segment.Offset = 0 && segment.Count = segment.Array.Length then
            segment.Array
        else
            let result = Array.zeroCreate segment.Count
            Array.Copy(segment.Array, segment.Offset, result, 0, segment.Count)
            result

open System
open System.IO
open System.Text

type Data =
| Empty
| String of string
| Binary of ArraySegment<byte>
with
    member this.RequireBinary () =
        match this with
        | Empty -> false
        | String _ -> false
        | Binary _ -> true

    member this.EncodeToBinary () =
        match this with
        | Empty -> Segment.empty
        | String s -> Encoding.UTF8.GetBytes s |> Segment.ofArray
        | Binary b -> b

    member this.EncodeToString () =
        match this with
        | Empty -> ""
        | String s -> s
        | Binary b ->
            // new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write)
            b |> Segment.toArray |> Convert.ToBase64String

    member this.GuessEncodeToStringLength() =
        match this with
        | Empty -> 0
        | String s -> s.Length
        | Binary b -> int (ceil (float b.Count / 3.) * 4.)

type PacketMessage =
| Open
| Close
| Ping of Data
| Pong of Data
| Message of Data
| Upgrade
| Noop
with
    member this.GetTypeId () =
        match this with
        | Open -> 0uy
        | Close -> 1uy
        | Ping _ -> 2uy
        | Pong _ -> 3uy
        | Message _ -> 4uy
        | Upgrade -> 5uy
        | Noop -> 6uy

    member this.GetData () =
        match this with
        | Open -> Empty
        | Close -> Empty
        | Ping x -> x
        | Pong x -> x
        | Message x -> x
        | Upgrade -> Empty
        | Noop -> Empty

    member this.EncodeToBinary () =
        let typeId = this.GetTypeId()
        let data = this.GetData().EncodeToBinary()
        let result = Array.zeroCreate (data.Count + 1)
        result.[0] <- typeId
        Array.Copy(data.Array, data.Offset, result, 1, data.Count)
        result

    member this.EncodeToString () =
        let data = this.GetData()
        let header = if data.RequireBinary() then "b" else ""
        header + this.GetTypeId().ToString() + data.EncodeToString()

type Payload =
| Payload of PacketMessage list
with
    member this.GetMessages() =
        match this with | Payload m -> m

    member this.EncodeToBinary () =
        let messages = this.GetMessages()
        let stream = new MemoryStream()
        for message in messages do
            let requireBinary = message.GetData().RequireBinary()
            stream.WriteByte(if requireBinary then 1uy else 0uy)
            let binData = if requireBinary then message.EncodeToBinary() else Encoding.UTF8.GetBytes(message.EncodeToString())
            let sizeString = binData.Length.ToString()
            for chr in sizeString do
                stream.WriteByte(Byte.Parse(string chr))
            stream.WriteByte(255uy)
            stream.Write(binData, 0, binData.Length)
        stream.ToArray()
                

    member this.EncodeToString() =
        let messages = this.GetMessages()
        if messages.Length = 0 then
            "0:"
        else
            let guess = messages |> List.fold (fun acc x -> acc + x.GetData().GuessEncodeToStringLength() + 6) 0
            let builder = StringBuilder(guess)
            for message in messages do
                let messageStr = message.EncodeToString()
                let consideredLenth = if message.GetData().RequireBinary() then messageStr.Length - 1 else messageStr.Length
                builder.Append(consideredLenth) |> ignore
                builder.Append(':') |> ignore
                if message.GetData().RequireBinary() then
                    builder.Append('b') |> ignore        
                builder.Append(messageStr) |> ignore
            
            builder.ToString()
