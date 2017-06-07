module SocketIoSuave.SocketIo.Engine

open System
open Suave
open Suave.CORS
open Suave.Operators
open SocketIoSuave.EngineIo.Protocol
open SocketIoSuave
open System.Security.Cryptography
open Suave.Logging
open Suave.Logging.Message
open SocketIoSuave.SocketIo
open SocketIoSuave.SocketIo.Protocol
open SocketIoSuave.EngineIo
open SocketIoSuave.EngineIo.Engine
open System.Collections.Generic
open System.Threading.Tasks

type SocketIoConfig =
    {
        EngineConfig: EngineIoConfig
    }

let initialPackets =
    { Packet.Type = PacketType.Connect; Namespace = "/"; EventId = None; Data = [] }
    |> PacketEncoder.encode
    |> List.map Message

let emptySocketIoConfig =
    {
        EngineConfig =
            { EngineIoConfig.empty with
                Path = "/socket.io/"
                InitialPackets = initialPackets
                PingInterval = TimeSpan.FromSeconds(3.)
                PingTimeout = TimeSpan.FromSeconds(10.)
            }
    }

let private log = Log.create "SocketIoSuave.SocketIo"

/// A connected Socket.IO socket
type ISocketIoSocket =
    /// Get the current socket Id
    abstract member Id: SocketId with get
    
    /// Receive the next packet on the socket, or None if the socket disconnected
    abstract member Receive: unit -> Async<Packet option>
    
    /// Send a packet to the current socket
    abstract member Send: Packet -> unit
    
    /// Send a packet to all other connected sockets
    abstract member Broadcast: Packet -> unit
    
    /// Request to close the socket
    abstract member Close: unit -> unit

type private IncomingCommunication =
    | NewIncomming of Packet
    | ReadIncomming of AsyncReplyChannel<Packet option>
    | CloseIncomming

type private SocketIoSocket(engineSocket: IEngineIoSocket, handlePackets: ISocketIoSocket -> Async<unit>) as this =
    let setSocketIdField = setField "socketId" (engineSocket.Id)

    let closeLock = new obj()
    let mutable closed = false

    // Packet decoding agent
    let incomming = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
        let rec loop (messages: Queue<Packet>) (currentReplyChan: AsyncReplyChannel<Packet option> option) = async {
            let! msg = inbox.Receive()

            match msg with
            | NewIncomming msg ->
                log.verbose (eventX "{socketId} NewIncomming {msg}" >> Message.setFieldValue "msg" msg >> setSocketIdField)
                match currentReplyChan with
                | Some(chan) ->
                    chan.Reply(Some msg)
                | None ->
                    messages.Enqueue msg

                return! loop messages None
            | ReadIncomming rep ->
                match messages.Count = 0, currentReplyChan with
                | true, None-> return! loop messages (Some(rep))
                | false, None ->
                    let msg = messages.Dequeue()
                    rep.Reply (Some msg)
                    return! loop messages None
                | _, Some(_) -> failwith "Don't cross the beams !"
            | CloseIncomming ->
                log.verbose (eventX "{socketId} CloseIncomming" >> setSocketIdField)
                match currentReplyChan with
                | Some(chan) -> chan.Reply(None)
                | None -> ()
                return ()
        }

        loop (new Queue<Packet>()) None
        )

    let mutable task: Task = null

    let rec handle decoderState : Async<unit> = async {
        let! message = engineSocket.Read()
        match message with
        | Some(enginePacket) ->
            let newPacket, newState = Protocol.PacketDecoder.step enginePacket decoderState
            match newPacket with
            | Some newPacket -> incomming.Post(NewIncomming newPacket)
            | None -> ()
            return! handle newState
        | None ->
            log.verbose (eventX "{socketId} Read returned None, finishing" >> setSocketIdField)
            this.Close()
            return ()
    }

    let send packet =
        let content = Protocol.PacketEncoder.encode packet
        engineSocket.Send(content)

    let broadcast packet =
        let content = Protocol.PacketEncoder.encode packet
        engineSocket.Broadcast(content)
        
    let read () =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                incomming.PostAndTryAsyncReply(ReadIncomming)
                |> Async.map Option.flattern)

    let onMailBoxError (x: Exception) =
        log.error (eventX "{socketId} Exception {exception}" >> Message.setFieldValue "exception" x >> setSocketIdField)
        this.Close()

    do
        incomming.Error.Add(onMailBoxError)

    member __.Handle() =
        // Start the async handler for this socket on the threadpool
        task <- handlePackets (this) |> Async.StartAsTask
        
        // When the handler finishes, close the socket
        task.ContinueWith(fun _ -> 
            log.verbose (eventX "{socketId} Handler finished, will close" >> setSocketIdField)
            this.Close()) |> ignore

        // Start our packet decoding loop
        handle Protocol.PacketDecoder.empty

    member __.Close() =
        lock closeLock (fun _ ->
            if not closed then
                closed <- true
                engineSocket.Close()
                incomming.Post CloseIncomming)

    interface ISocketIoSocket with
        member __.Id with get () = engineSocket.Id
        member __.Receive() = read ()
        member __.Send(packet) = send packet
        member __.Broadcast(packet) = broadcast packet
        member __.Close() = this.Close()

type SocketIo(handlePackets: ISocketIoSocket -> Async<unit>) =
    let initPacket = { Packet.Type = PacketType.Connect; Namespace = "/"; EventId = None; Data = [] }
    let engineConfig =
        { EngineIoConfig.empty with
            Path = "/socket.io/"
            InitialPackets = initPacket |> PacketEncoder.encode |> List.map Message
        }

    let handleSocket (engineSocket: IEngineIoSocket) =
        let socket = new SocketIoSocket(engineSocket, handlePackets)
        socket.Handle()

    let engine = new EngineIo(engineConfig, { handleSocket = handleSocket } )

    let handle: WebPart =
        choose [
            EmbededFiles.handleInPath engineConfig.Path
            suaveEngineIo engine engineConfig
        ]

    /// Version of the socket.io protocol
    member val Version = Protocol.version
    member val WebPart = handle

    /// Broadcast the same packet to every connected client
    member __.Broadcast(packet: Packet) =
        let content = Protocol.PacketEncoder.encode packet
        engine.Broadcast(None, content)