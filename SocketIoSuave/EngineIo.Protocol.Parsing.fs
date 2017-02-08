[<AutoOpen>]
module SocketIoSuave.EngineIo.Protocol.Parsing

open SocketIoSuave

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PacketContent =
    open System.Text
    open System

    let requireBinary = function
    | Empty -> false
    | TextPacket _ -> false
    | BinaryPacket _ -> true

    let encodeToBinary = function
    | Empty -> Segment.empty
    | TextPacket s ->
        if isNull s || s.Length = 0 then
            Segment.empty
        else
            Encoding.UTF8.GetBytes s |> Segment.ofArray
    | BinaryPacket b -> b

    let encodeToString = function
    | Empty -> ""
    | TextPacket s -> s
    | BinaryPacket b ->
        // new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write)
        b |> Segment.toArray |> Convert.ToBase64String

    let guessEncodeToStringLength = function
    | Empty -> 0
    | TextPacket s -> if isNull s then 0 else s.Length
    | BinaryPacket b -> int (ceil (float b.Count / 3.) * 4.)

    let decodeFromBinary isBinary (dataBytes: ByteSegment) =
        if dataBytes.Count = 0 then
            Empty
        else if isBinary then
            BinaryPacket(dataBytes)
        else
            TextPacket(Encoding.UTF8.GetString(dataBytes.Array, dataBytes.Offset, dataBytes.Count))

    let decodeFromString isBinary (str: string) offset count =
        if count = 0 then
            Empty
        else if isBinary then
            let subString = str.Substring(offset, count)
            let data = Convert.FromBase64String(subString)
            BinaryPacket(data |> Segment.ofArray)
        else
            let subString = str.Substring(offset, count)
            TextPacket(subString)

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PacketMessage =
    open System

    let getTypeId = function
    | Open _ -> 0uy
    | Close -> 1uy
    | Ping _ -> 2uy
    | Pong _ -> 3uy
    | Message _ -> 4uy
    | Upgrade -> 5uy
    | Noop -> 6uy

    let getData = function
    | Open handshake -> TextPacket (OpenHandshake.encode handshake)
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
    | x -> x |> getData |> PacketContent.requireBinary

    let encodeToBinary packet =
        // Data
        let data = packet |> getData
        let dataBytes = data |> PacketContent.encodeToBinary

        // Type ID
        let typeIdRaw = getTypeId packet
        let typeId = if data |> PacketContent.requireBinary then typeIdRaw else byte (typeIdRaw.ToString().[0])

        // Build
        let result = Array.zeroCreate (dataBytes.Count + 1)
        result.[0] <- typeId
        Array.Copy(dataBytes.Array, dataBytes.Offset, result, 1, dataBytes.Count)
        result |> Segment.ofArray

    let encodeToString packet =
        // Data
        let data = packet |> getData
        let dataString = data |> PacketContent.encodeToString

        // Type ID
        let typeIdRaw = getTypeId packet
        let typeId = typeIdRaw.ToString()
        let header = if data |> PacketContent.requireBinary then "b" else ""

        // Build
        header + typeId + dataString

    let private failIfData data typeId =
        match data with
        | Empty -> ()
        | _ -> failwithf "Packet of type %A doesn't support data" typeId

    let private fromData typeId data=
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
        let typeIdRaw = data |> Segment.valueAt 0
        let typeId = if isBinary then typeIdRaw else Byte.Parse (string (char typeIdRaw))
        let dataBytes = data |> Segment.skip 1
        let data = dataBytes |> PacketContent.decodeFromBinary isBinary
                
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
        let data = PacketContent.decodeFromString isBinary str currentOffset dataCount
        
        fromData typeId data

    let decodeFromString (str: string) = decodeFromStringSubset 0 str.Length str

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Payload =
    open System.IO
    open System.Text
    open System

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
                    Encoding.UTF8.GetBytes(message |> PacketMessage.encodeToString) |> Segment.ofArray
            let sizeString = binData.Count.ToString()
            for chr in sizeString do
                stream.WriteByte(Byte.Parse(string chr))
            stream.WriteByte(255uy)
            stream.Write(binData.Array, binData.Offset, binData.Count)
        stream.ToArray() |> Segment.ofArray

    let encodeToString payload =
        let messages = payload |> getMessages
        if messages.Length = 0 then
            "0:"
        else
            let sizeGuess = messages |> List.fold (fun acc m -> acc + (m |> PacketMessage.getData |> PacketContent.guessEncodeToStringLength) + 6) 0
            let builder = StringBuilder(sizeGuess)
            for message in messages do
                let messageStr = message |> PacketMessage.encodeToString
                builder.Append(messageStr.Length) |> ignore
                builder.Append(':') |> ignore
                builder.Append(messageStr) |> ignore
            
            builder.ToString()

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
                    let packet = PacketMessage.decodeFromBinary isBinary subset
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
                    let packet = PacketMessage.decodeFromStringSubset currentPos size str
                    currentPos <- currentPos + size
                    yield packet
        ]

        Payload(packets)