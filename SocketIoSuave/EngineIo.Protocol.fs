module SocketIoSuave.EngineIo.Protocol

open SocketIoSuave
open SocketIoSuave.EngineIo

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private PacketContent =
    let inline requireBinary content =
        match content with 
        | Empty -> false
        | TextPacket _ -> false
        | BinaryPacket _ -> true

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private OpenHandshake =
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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private PacketMessage =
    let inline getData packetMessage =
        match packetMessage with
        | Open handshake -> TextPacket (OpenHandshake.encode handshake)
        | Close -> Empty
        | Ping x -> x
        | Pong x -> x
        | Message x -> x
        | Upgrade -> Empty
        | Noop -> Empty

    let inline requireBinary packetMessage =
        match packetMessage with
        | Open _ -> false
        | Close -> false
        | Upgrade -> false
        | Noop -> false
        | x -> x |> getData |> PacketContent.requireBinary

module PacketMessageDecoder =
    open System
    open System.Text

    let decodeContentFromBinary isBinary (dataBytes: ByteSegment) =
        if dataBytes.Count = 0 then
            Empty
        else if isBinary then
            BinaryPacket(dataBytes)
        else
            TextPacket(Encoding.UTF8.GetString(dataBytes.Array, dataBytes.Offset, dataBytes.Count))

    let decodeContentFromString isBinary (str: string) offset count =
        if count = 0 then
            Empty
        else if isBinary then
            let subString = str.Substring(offset, count)
            let data = Convert.FromBase64String(subString)
            BinaryPacket(data |> Segment.ofArray)
        else
            let subString = str.Substring(offset, count)
            TextPacket(subString)

    let private failIfData data typeId =
        match data with
        | Empty -> ()
        | _ -> failwithf "Packet of type %A doesn't support data" typeId

    let inline private fromData typeId data=
        match typeId with
        | 0uy ->
            match data with
            | TextPacket str ->
                let handshake = OpenHandshake.decode str
                Open handshake
            | _ -> failwithf "Unexpected content for handshake: %A" data
        | 1uy ->
            failIfData data typeId
            Close
        | 2uy ->
            Ping(data)
        | 3uy ->
            Pong(data)
        | 4uy ->
            Message(data)
        | 5uy ->
            failIfData data typeId
            Upgrade
        | 6uy ->
            failIfData data typeId
            Noop
        | _ -> failwithf "Unknown packet type: %A" typeId

    let decodeFromBinary isBinary (data: ByteSegment) =
        let typeIdRaw = Segment.valueAt 0 data
        let typeId = if isBinary then typeIdRaw else Byte.Parse (string (char typeIdRaw))
        let dataBytes = Segment.skip 1 data
        let data = decodeContentFromBinary isBinary dataBytes
                
        fromData typeId data

    let decodeFromStringSubset offset count (str: string) =
        if offset < 0 then raise (ArgumentOutOfRangeException("offset", offset, "offset is negative"))
        if count < 0 then raise (ArgumentOutOfRangeException("count", offset, "count is negative"))
        if offset + count > str.Length then raise (ArgumentException("The designated range is out of the string"))

        let mutable currentOffset = offset
        let firstChar = str.[currentOffset]
        currentOffset <- currentOffset + 1
        let isBinary = firstChar = 'b'
        let typeIdChar =
            if isBinary then
                let c = str.[currentOffset]
                currentOffset <- currentOffset + 1
                c
            else
                firstChar
        let typeId = typeIdChar |> string |> Byte.Parse

        let dataCount = count - (currentOffset - offset)
        let data = decodeContentFromString isBinary str currentOffset dataCount
        
        fromData typeId data

    let decodeFromString (str: string) = decodeFromStringSubset 0 str.Length str

module PayloadDecoder =
    open System.Text
    open System

    let decodeFromBinary (data: ByteSegment) =
        let mutable currentPos = data.Offset
        let sizeStringBuilder = StringBuilder(5)
        let packets = [
            while currentPos < data.Offset + data.Count do
                let isBinary = data.Array.[currentPos] = 1uy
                currentPos <- currentPos + 1
                sizeStringBuilder.Clear() |> ignore
                while data.Array.[currentPos] <> 255uy do
                    let currentByte = data.Array.[currentPos]
                    sizeStringBuilder.Append(currentByte.ToString()) |> ignore
                    currentPos <- currentPos + 1
                currentPos <- currentPos + 1
                let size = Int32.Parse(sizeStringBuilder.ToString())
                if size <> 0 then
                    let subset = data |> Segment.subset currentPos size
                    let packet = PacketMessageDecoder.decodeFromBinary isBinary subset
                    currentPos <- currentPos + size
                    yield packet
        ]

        Payload(packets)

    let decodeFromString (str: string) =
        let mutable currentPos = 0
        let sizeStringBuilder = StringBuilder(5)
        let packets = [
            while currentPos < str.Length do
                sizeStringBuilder.Clear() |> ignore
                while currentPos < str.Length && str.[currentPos] <> ':' do
                    sizeStringBuilder.Append(str.[currentPos]) |> ignore
                    currentPos <- currentPos + 1
                currentPos <- currentPos + 1
                let size = Int32.Parse(sizeStringBuilder.ToString())
                if size <> 0 then
                    let packet = PacketMessageDecoder.decodeFromStringSubset currentPos size str
                    currentPos <- currentPos + size
                    yield packet
        ]

        Payload(packets)

module PacketMessageEncoder =
    open System
    open System.Text

    let inline private encodeContentToBinary content =
        match content with 
        | Empty -> Segment.empty
        | TextPacket s ->
            if isNull s || s.Length = 0 then
                Segment.empty
            else
                Encoding.UTF8.GetBytes s |> Segment.ofArray
        | BinaryPacket b -> b

    let inline private encodeContentToString content =
        match content with 
        | Empty -> ""
        | TextPacket s -> s
        | BinaryPacket b ->
            // new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write)
            b |> Segment.toArray |> Convert.ToBase64String

    let inline private guessEncodeContentToStringLength content =
        match content with 
        | Empty -> 0
        | TextPacket s -> if isNull s then 0 else s.Length
        | BinaryPacket b -> int (ceil (float b.Count / 3.) * 4.)

    let inline private getMessageTypeId packetMessage =
        match packetMessage with
        | Open _ -> 0uy
        | Close -> 1uy
        | Ping _ -> 2uy
        | Pong _ -> 3uy
        | Message _ -> 4uy
        | Upgrade -> 5uy
        | Noop -> 6uy

    let encodeToBinary packet =
        // Data
        let data = PacketMessage.getData packet
        let dataBytes = encodeContentToBinary data

        // Type ID
        let typeIdRaw = getMessageTypeId packet
        let typeId = if data |> PacketContent.requireBinary then typeIdRaw else byte (typeIdRaw.ToString().[0])

        // Build
        let result = Array.zeroCreate (dataBytes.Count + 1)
        result.[0] <- typeId
        Array.Copy(dataBytes.Array, dataBytes.Offset, result, 1, dataBytes.Count)
        result |> Segment.ofArray

    let encodeToString packet =
        // Data
        let data = PacketMessage.getData packet
        let dataString = encodeContentToString data

        // Type ID
        let typeIdRaw = getMessageTypeId packet
        let typeId = typeIdRaw.ToString()
        let header = if data |> PacketContent.requireBinary then "b" else ""

        // Build
        header + typeId + dataString

    let guessEncodeToStringLength packet =
        let data = PacketMessage.getData packet
        // One for type Id and one for the potential 'b'
        guessEncodeContentToStringLength data + 2

    let requireBinary packet =
        packet |> PacketMessage.getData |> PacketContent.requireBinary

module PayloadEncoder =
    open System
    open System.IO
    open System.Text

    let encodeToBinary payload =
        let messages = payload |> Payload.getMessages
        let stream = new MemoryStream()
        for message in messages do
            let requireBinary = message |> PacketMessage.requireBinary
            stream.WriteByte(if requireBinary then 1uy else 0uy)
            let binData =
                if requireBinary then
                    message |> PacketMessageEncoder.encodeToBinary
                else
                    Encoding.UTF8.GetBytes(message |> PacketMessageEncoder.encodeToString) |> Segment.ofArray
            let sizeString = binData.Count.ToString()
            for chr in sizeString do
                stream.WriteByte(Byte.Parse(string chr))
            stream.WriteByte(255uy)
            stream.Write(binData.Array, binData.Offset, binData.Count)
        stream.ToArray() |> Segment.ofArray

    let encodeToString payload =
        let messages = payload |> Payload.getMessages
        if messages.Length = 0 then
            "0:"
        else
            let sizeGuess = messages |> List.fold (fun acc m -> acc + (PacketMessageEncoder.guessEncodeToStringLength m) + 4) 0
            let builder = StringBuilder(sizeGuess)
            for message in messages do
                let messageStr = message |> PacketMessageEncoder.encodeToString
                builder.Append(messageStr.Length) |> ignore
                builder.Append(':') |> ignore
                builder.Append(messageStr) |> ignore
            
            builder.ToString()