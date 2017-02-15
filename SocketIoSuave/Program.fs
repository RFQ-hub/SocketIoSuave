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
open SocketIoSuave.EngineIo.Engine
open System.Collections.Generic
open System.Threading.Tasks

let handlePacket (packet: PacketMessage) =
    printfn "Received: %A" packet

let okJson x : WebPart = Writers.setMimeType "application/json" >=> Successful.OK x

type SocketIoConfig =
    {
        EngineConfig: EngineIoConfig
    }

let emptySocketIoConfig =
    {
        EngineConfig =
            { EngineIoConfig.empty with
                Path = "/socket.io/"
                InitialPackets = { Packet.Type = PacketType.Connect; Namespace = "/"; EventId = None; Data = [] } |> Packet.encode |> List.map Message
            }
    }

let private log = Log.create "socket.io"

type ISocketIoSocket =
    abstract member Id: SocketId with get
    abstract member Receive: unit -> Async<Packet option>
    abstract member Send: Packet -> unit
    abstract member Close: unit -> unit

type private IncomingCommunication =
    | NewIncomming of Packet
    | ReadIncomming of AsyncReplyChannel<Packet option>
    | CloseIncomming

type private SocketIoSocket(engineSocket: IEngineIoSocket, handlePackets: ISocketIoSocket -> Async<unit>) as this =
    let logError s = log.error (eventX (sprintf "[%s] %s" (engineSocket.Id.ToString()) s))
    let logDebug s = log.debug (eventX (sprintf "[%s] %s" (engineSocket.Id.ToString()) s))
    let logWarn s = log.warn (eventX (sprintf "[%s] %s" (engineSocket.Id.ToString()) s))

    let closeLock = new obj()
    let mutable closed = false

    // Packet decoding agent
    let incomming = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
        let rec loop (messages: Queue<Packet>) (currentReplyChan: AsyncReplyChannel<Packet option> option) = async {
            let! msg = inbox.Receive()

            match msg with
            | NewIncomming msg ->
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
            return ()
    }

    let send packet =
        let content = Protocol.PacketEncoder.encode packet
        engineSocket.Send(content)
        
    let read () =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                incomming.PostAndTryAsyncReply(ReadIncomming)
                |> Async.map Option.flattern)

    let onMailBoxError (x: Exception) =
        logError (x.ToString())
        this.Close()

    do
        incomming.Error.Add(onMailBoxError)

    member __.Handle() =
        // Start the async handler for this socket on the threadpool
        task <- handlePackets (this) |> Async.StartAsTask
        
        // When the handler finishes, close the socket
        task.ContinueWith(fun _ -> this.Close()) |> ignore

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
        member __.Close() = this.Close()

type SocketIo(handlePackets: ISocketIoSocket -> Async<unit>) =
    let initPacket = { Packet.Type = PacketType.Connect; Namespace = "/"; EventId = None; Data = [] }
    let engineConfig =
        { EngineIoConfig.empty with
            Path = "/socket.io/"
            InitialPackets = initPacket |> Packet.encode |> List.map Message
        }

    let handleSocket (engineSocket: IEngineIoSocket) =
        let socket = new SocketIoSocket(engineSocket, handlePackets)
        socket.Handle()

    let engine = new EngineIo(engineConfig, { handleSocket = handleSocket } )

    let handle: WebPart = suaveEngineIo engine engineConfig

    /// Version of the socket.io protocol
    member val Version = Protocol.version
    member val WebPart = handle

[<EntryPoint>]
let main argv = 
    let socketio handlePackets = SocketIo(handlePackets).WebPart

    let rec handlePacket (socket: ISocketIoSocket) = async {
        let! p = socket.Receive()
        match p with
        |Some packet ->
            log.info (eventX (sprintf "< %A" packet))
            match packet.EventId with
            | Some id -> socket.Send({ Packet.ofType Ack with EventId = Some id })
            | None -> ()
            return! handlePacket socket
        |None -> return ()
    }

    let app =
        choose [
            //serveEngineIo { EngineIoConfig.empty with Path = "/socket.io/"; InitialPackets = initialPackets }
            socketio handlePacket
            // serveSocketIo emptySocketIoConfig
            Successful.OK "Hello World!"
        ]
    let suaveConf = { defaultConfig with logger = Targets.create Debug [| "Suave" |] }
    startWebServer suaveConf app
    0
