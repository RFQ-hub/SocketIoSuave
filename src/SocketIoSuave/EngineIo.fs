module SocketIoSuave.EngineIo.Engine

open System
open System.Security.Cryptography
open System.Collections.Generic
open Suave.Logging
open Suave.Logging.Message
open Suave
open Suave.CORS
open Suave.Operators
open Suave.Cookie
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Chessie.ErrorHandling
open System.Threading.Tasks
open SocketIoSuave
open SocketIoSuave.EngineIo
open SocketIoSuave.EngineIo.Protocol

let private log = Log.create "engine.io"

type SocketId = SocketId of string
with
    override x.ToString() = match x with | SocketId s -> s

type EngineIoConfig =
    {
        Path: string
        Upgrades: Transport list
        PingTimeout: TimeSpan
        PingInterval: TimeSpan
        CookieName: string option
        CookiePath: string option
        CookieHttpOnly: bool
        RandomNumberGenerator: RandomNumberGenerator
        
        /// Packets sent along with the handshake
        InitialPackets: PacketMessage list
    }

    with static member empty = {
            Path = "/engine.io/"
            CookieName = Some "io"
            CookiePath = Some "/"
            CookieHttpOnly = false
            Upgrades = [Websocket]

            PingTimeout = TimeSpan.FromSeconds(60.)
            PingInterval = TimeSpan.FromSeconds(25.)
            RandomNumberGenerator = RandomNumberGenerator.Create()
            InitialPackets = []
        }
        
let private mkCookie (socketId: SocketId) config =
    match config.CookieName with
    | Some(cookieName) ->
        Some { HttpCookie.empty with name = cookieName; value = socketId.ToString(); path = config.CookiePath; httpOnly = config.CookieHttpOnly }
    | None -> None

let private mkHandshake socketId config =
    {
        Sid = socketId
        Upgrades = config.Upgrades |> List.map Transport.toString |> Array.ofList
        PingTimeout = int config.PingTimeout.TotalMilliseconds
        PingInterval = int config.PingInterval.TotalMilliseconds
    }

type Error =
    | UnknownSessionId
    | Unknown

type IEngineIoSocket =
    abstract member Id: SocketId with get
    abstract member Read: unit -> Async<PacketContent option>
    abstract member Send: PacketContent seq -> unit
    abstract member Send: PacketContent -> unit
    abstract member Broadcast: PacketContent seq -> unit
    abstract member Broadcast: PacketContent -> unit
    abstract member Close: unit -> unit

type private IncomingCommunication =
    | NewIncomming of PacketMessage
    | ReadIncomming of AsyncReplyChannel<PacketContent option>
    | CloseIncomming

type private OutgoingCommunication =
    | NewOutgoing of PacketMessage list
    | ReadOutgoing of AsyncReplyChannel<Payload>
    | Upgrading of WebSocket
    | Upgraded
    | CloseOutgoing

type private SocketEngineCommunication =
    {
        socketClosing: SocketId -> unit
        broadcast: PacketContent seq -> unit
    }

type private EngineIoSocket(id: SocketId, pingTimeout: TimeSpan, comms: SocketEngineCommunication, handleSocket: IEngineIoSocket -> Async<unit>) as this =
    let logVerbose s = log.debug (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logError s = log.error (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logDebug s = log.debug (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logWarn s = log.warn (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    
    let closeLock = new obj()
    let mutable closed = false
    let mutable transport = Polling
    let mutable task: Task = null

    let timeoutInMs = int pingTimeout.TotalMilliseconds
    let w = Diagnostics.Stopwatch()

    let pingTimeoutTimer =
        let timer = new System.Timers.Timer()
        timer.AutoReset <- false
        timer.Interval <- pingTimeout.TotalMilliseconds
        timer.Elapsed.Add(fun _ ->
            logDebug (sprintf "Ping timeout, closing socket. Since last reset= %O" w.Elapsed)
            this.Close())
        timer

    let setPingTimeout () =
        pingTimeoutTimer.Stop()
        w.Restart()
        pingTimeoutTimer.Start()

    let incomming = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
        let rec loop (messages: Queue<PacketContent>) (currentReplyChan: AsyncReplyChannel<PacketContent option> option) = async {
            let! msg = inbox.Receive()

            match msg with
            | NewIncomming msg ->
                // We're nice an consider any message as a ping for the purpose of connection timeouts
                setPingTimeout()
                logVerbose (sprintf "NewIncomming %A" msg)
                
                match msg with
                | Ping data ->
                    this.AddOutgoing([Pong(data)])
                    return! loop messages currentReplyChan
                | Open _ ->
                    logWarn "Open received during communication"
                    return! loop messages currentReplyChan
                | Upgrade ->
                    logWarn "Upgrade received on an already open transport"
                    return! loop messages currentReplyChan
                | Close -> this.Close()
                | Message content ->
                    match currentReplyChan with
                    | Some(chan) ->
                        chan.Reply(Some content)
                    | None ->
                        messages.Enqueue content

                    return! loop messages None
                | Pong _
                | Noop -> return! loop messages currentReplyChan
            | ReadIncomming rep ->
                match messages.Count = 0, currentReplyChan with
                | true, None-> return! loop messages (Some(rep))
                | false, None ->
                    let msg = messages.Dequeue()
                    rep.Reply (Some msg)
                    return! loop messages None
                | _, Some(_) ->
                    failwith "Don't cross the beams !"
            | CloseIncomming ->
                logVerbose "CloseIncomming"
                match currentReplyChan with
                | Some(chan) -> chan.Reply(None)
                | None -> ()
                return ()
        }

        loop (new Queue<PacketContent>()) None
        )

    let sendOnWebSocket (ws: WebSocket) message =
        let binary = PacketMessageEncoder.requireBinary message
        if binary then
            let bytes = PacketMessageEncoder.encodeToBinary message
            ws.send Binary bytes true |> Async.Ignore // TODO: Don't ignore errors
        else
            let bytes = PacketMessageEncoder.encodeToString message |> UTF8.bytes |> Segment.ofArray
            ws.send Text bytes true |> Async.Ignore // TODO: Don't ignore errors

    let outgoing = MailboxProcessor<OutgoingCommunication>.Start(fun inbox ->
        let rec loop messages (currentReplyChan: AsyncReplyChannel<Payload> option) (websocket: WebSocket option) (upgrading: bool) = async {
            let! msg = inbox.Receive()
            match msg with
            | NewOutgoing newMessages ->
                logVerbose (sprintf "NewOutgoing %A" newMessages)
                match websocket, upgrading with
                | None, _ ->
                    let allMessages = List.append newMessages messages
                    match currentReplyChan with
                    | Some(chan) ->
                        chan.Reply(Payload allMessages)
                        return! loop [] None websocket upgrading
                    | None ->
                        return! loop allMessages None websocket upgrading
                | Some _, true ->
                    let allMessages = List.append newMessages messages
                    return! loop allMessages None websocket upgrading
                | Some ws, false ->
                    for message in newMessages do
                        do! sendOnWebSocket ws message
                    return! loop [] None websocket upgrading
            | ReadOutgoing rep ->
                match websocket with
                | Some _ ->
                    rep.Reply(Payload([Noop]))
                    return! loop messages currentReplyChan websocket upgrading
                | None ->
                    match currentReplyChan with
                    | Some otherRep ->
                        // Two requests crossed, if the client behave normally the first one had a network error and
                        // timeouted, but just in case we still answer something
                        logWarn "[ReadOutgoing] Requests crossed, previous will get an empty payload"
                        otherRep.Reply(Payload([]))
                    | None -> ()

                    match messages with
                    | []-> return! loop [] (Some(rep)) None false
                    | messages ->
                        rep.Reply(Payload(messages))
                        return! loop [] None  None false
            | Upgrading ws ->
                match currentReplyChan with
                | Some otherRep ->
                    otherRep.Reply(Payload([Noop]))
                | None -> ()
                return! loop messages None (Some ws) true
            | Upgraded ->
                match websocket, upgrading with
                | Some ws, true ->
                    for message in messages do
                        do! sendOnWebSocket ws message
                    return! loop [] None websocket false
                | _ -> () // Logic error ?
            | CloseOutgoing ->
                logVerbose "CloseOutgoing"
                match currentReplyChan with
                | Some(chan) -> chan.Reply(Payload([Close]))
                | None -> ()
                match websocket with
                | Some(websocket) ->
                    do! sendOnWebSocket websocket Close
                    // TODO: Really close the websocket
                | None -> ()
                return ()
        }

        loop [] None None false
    )

    let onMailBoxError (x: Exception) =
        logError (sprintf "Exception, will close: %O" (x.ToString()))
        this.Close()

    do
        incomming.DefaultTimeout <- timeoutInMs * 2
        incomming.Error.Add(onMailBoxError)
        outgoing.DefaultTimeout <- timeoutInMs * 2
        outgoing.Error.Add(onMailBoxError)

    member val Id = id
    member __.Transport with get() = transport
    member __.Start() =
        setPingTimeout ()
        
        // Start the async handler for this socket on the threadpool
        task <- handleSocket (this) |> Async.StartAsTask
        
        // When the handler finishes, close the socket
        task.ContinueWith(fun _ ->
            logDebug "Handler finished, will close"
            this.Close()) |> ignore

    member __.ReadIncomming() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                incomming.PostAndAsyncReply(ReadIncomming))

    member __.ReadOutgoing() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                outgoing.PostAndTryAsyncReply(ReadOutgoing, timeoutInMs))

    member __.AddOutgoing(messages) =
        outgoing.Post (NewOutgoing messages)

    member __.AddIncomming(msg) =
        incomming.Post (NewIncomming msg)

    member __.Close() =
        lock closeLock (fun _ ->
            if not closed then
                logDebug "Closing"
                closed <- true
                comms.socketClosing id
                pingTimeoutTimer.Stop()
                pingTimeoutTimer.Dispose()
                incomming.Post CloseIncomming
                outgoing.Post CloseOutgoing
                logDebug "Closed")
    
    member __.Send(messages) = this.AddOutgoing(messages |> List.map Message)
    member __.Send(messages) = this.AddOutgoing(messages |> Seq.map Message |> List.ofSeq)
    member __.Send(message) = this.AddOutgoing([Message message])
    member __.StartUpgrade(ws: WebSocket) = outgoing.Post (Upgrading ws)
    member __.FinishUpgrade() =
        transport <- Websocket
        outgoing.Post (Upgraded)

    interface IEngineIoSocket with
        member __.Id with get () = this.Id
        member __.Read() = this.ReadIncomming()
        member __.Send(messages: PacketContent seq) = this.Send(messages)
        member __.Send(message: PacketContent) = this.Send(message)
        member __.Broadcast(messages: PacketContent seq) = comms.broadcast messages
        member __.Broadcast(message: PacketContent) = comms.broadcast ([message] :> _ seq)
        member __.Close() = this.Close()

let inline private badAsync err = Async.result (Bad [err]) |> AR
let inline private okAsync ok = Async.result (Ok(ok,[])) |> AR

type EngineApp = 
    {
        handleSocket: IEngineIoSocket -> Async<unit>
    }

type private RequestContext =
    {
        Transport: Transport option
        JsonPIndex: int option
        SessionId: string option
        SupportsBinary: bool
        IsBinary: bool
    }

let private queryParam name (req: HttpRequest) =
    req.query
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.bind snd

let private header name (req: HttpRequest) =
    req.headers
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.map snd

let private getContext (req: HttpRequest): RequestContext =
    {
        Transport = req |> queryParam "transport" |> Option.bind(Transport.fromString)
        JsonPIndex = req |> queryParam "j" |> Option.bind Option.parseInt
        SessionId = req |> queryParam "sid"
        SupportsBinary = req |> queryParam "b64" |> Option.bind Option.parseIntAsBool |> Option.defaultArg false
        IsBinary = req |> header "content-type" = Some "application/octet-stream"
    }

let private setCookieSync (cookie : HttpCookie) (response: HttpResult) =
    let notSetCookie : string * string -> bool =
        fst >> (String.equalsOrdinalCI Headers.Fields.Response.setCookie >> not)

    let cookieHeaders =
        response.cookies
        |> Map.put cookie.name cookie // possibly overwrite
        |> Map.toList
        |> List.map (snd >> HttpCookie.toHeader)

    let headers' =
      cookieHeaders
      |> List.fold (fun headers header ->
          (Headers.Fields.Response.setCookie, header) :: headers)
          (response.headers |> List.filter notSetCookie)

    { response with headers = headers' }

let private setCookieSync' (cookie : HttpCookie option) (response: HttpResult) =
    match cookie with
    | Some cookie -> setCookieSync cookie response
    | None -> response

let inline private setHeader name value response =
    let headers = (name, value)::response.headers
    { response with headers = headers }

let inline private setUniqueHeader name value response =
    let headers = response.headers |> List.filter (fun (headerName, _) -> not (String.equalsOrdinalCI headerName name) )
    let headers = (name, value)::headers
    { response with headers = headers }

let inline private setContentBytes bytes response =
    { response with content = HttpContent.Bytes bytes}

let inline private bytesResponse (code: HttpCode) (bytes: byte[]) =
    { status = code.status; headers = []; content = Bytes bytes; writePreamble = true }

let inline private simpleResponse (code: HttpCode) (message: string) =
    bytesResponse code (UTF8.bytes message)

/// Use CompareExchange to apply a mutation to a field.
/// Mutation must be pure & writes to the field should be rare compared to reads.
let private mutateField<'t when 't: not struct> (targetField: 't byref) (mutation: 't -> 't) =
    let mutable retry = true
    while retry do
        let before = targetField
        let newValue = mutation before
        let afterExchange = System.Threading.Interlocked.CompareExchange(&targetField, newValue, before)
        retry <- not (obj.ReferenceEquals(before, afterExchange))

type EngineIo(config, app: EngineApp) as this =
    let mutable sessions: Map<SocketId, EngineIoSocket> = Map.empty
    let idGenerator = Base64Id.create config.RandomNumberGenerator
    
    let socketTimeout = config.PingTimeout + config.PingInterval
    
    let tryGetSocket engineCtx =
        engineCtx.SessionId
        |> Option.map SocketId
        |> Option.bind (fun socketId -> Map.tryFind socketId sessions)

    let removeSocket (socketId: SocketId) =
        log.info (eventX (sprintf "Removing session with ID %s" (socketId.ToString())))
        mutateField &sessions (fun s -> s |> Map.remove socketId)

    let socketCommunications =
        {
            socketClosing = removeSocket
            broadcast = this.Broadcast
        }

    let payloadToResponse sid payload engineCtx =
        if engineCtx.SupportsBinary then
            bytesResponse HttpCode.HTTP_200 (payload |> PayloadEncoder.encodeToBinary |> Segment.toArray)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "application/octet-stream"
            |> setContentBytes (payload |> PayloadEncoder.encodeToBinary |> Segment.toArray)
        else
            bytesResponse HttpCode.HTTP_200 (payload |> PayloadEncoder.encodeToString |> UTF8.bytes)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "text/plain; charset=UTF-8"
            |> setContentBytes (payload |> PayloadEncoder.encodeToString |> System.Text.Encoding.UTF8.GetBytes)

    let handleGet' engineCtx: AsyncResult<SocketId*Payload, Error> =
        match engineCtx.SessionId with
        | None ->
            let socketIdString = idGenerator ()
            let socketId = SocketId socketIdString

            let socket = new EngineIoSocket(socketId, socketTimeout, socketCommunications, app.handleSocket)
            mutateField &sessions (fun s -> s |> Map.add socket.Id socket)
            socket.Start()
            
            log.info (eventX (sprintf "Creating session with ID %s" socketIdString))

            let handshake = mkHandshake socketIdString config
            let payload = Payload(Open(handshake) :: config.InitialPackets)
            okAsync (socketId, payload)
        | Some(sessionId) ->
            let socketId = SocketId sessionId
            match sessions |> Map.tryFind socketId with
            | Some(socket) -> asyncTrial {
                let! payload = socket.ReadOutgoing ()
                let payload' = defaultArg payload (Payload([]))
                log.debug (eventX (sprintf "%s <- %A" sessionId (Payload.getMessages payload')))
                return socketId, payload'
                }
            | None ->
                badAsync UnknownSessionId

    let errorsToHttp (result:Result<HttpResult, Error>): HttpResult =
        match result with 
        | Ok(response, _) -> response
        | Bad(errors) ->
            let error = errors |> List.tryHead |> Option.orDefault Unknown
            match error with
            | Unknown -> simpleResponse HttpCode.HTTP_500 "Unknown error"
            | UnknownSessionId -> simpleResponse HttpCode.HTTP_404 "Unknown session ID"

    let handleGet engineCtx =
        asyncTrial {
            let! x = handleGet' engineCtx
            let (socketId, payload) = x
            return payloadToResponse socketId payload engineCtx
        }

    let handlePost' (req: HttpRequest) engineCtx: Result<SocketId, Error> =
        match tryGetSocket engineCtx with
        | Some(socket) ->
            let payload =
                if engineCtx.SupportsBinary then
                    req.rawForm |> Segment.ofArray |> PayloadDecoder.decodeFromBinary
                else
                    req.rawForm |> Text.Encoding.UTF8.GetString |> PayloadDecoder.decodeFromString
            for message in payload |> Payload.getMessages do
                log.debug (eventX (sprintf "%s -> %A" (socket.Id.ToString()) message))
                socket.AddIncomming message
            ok socket.Id
        | None ->
            fail UnknownSessionId

    let handlePost engineCtx ctx =
        trial {
            let! socketId = handlePost' ctx engineCtx
            return simpleResponse HttpCode.HTTP_200 "" |> setCookieSync' (mkCookie socketId config)
        }

    let returnResponse ctx response =
        Some { ctx with response = response }

    let handleWebsocket (engineCtx: RequestContext) (webSocket : WebSocket) (context: HttpContext) = socket {
        let mutable loop = true

        while loop do
            let! msg = webSocket.read()

            match msg with
            | (Text, data, true) ->
                match tryGetSocket engineCtx with
                | Some(socket) ->
                    let str = UTF8.toString data
                    let packetMessage = PacketMessageDecoder.decodeFromString str
                    match socket.Transport with
                    | Websocket ->
                        // If we're already in websocket mode we enqueue the received message
                        socket.AddIncomming(packetMessage)
                    | _ ->
                        // Otherwise we only handle a small subset of messages
                        match packetMessage with
                        | Ping pingData when pingData = TextPacket "probe" ->
                            let answer = PacketMessageEncoder.encodeToString (Pong pingData) |> UTF8.bytes |> Segment.ofArray
                            do! webSocket.send Text answer true
                            socket.StartUpgrade(webSocket)
                        | Upgrade ->
                            socket.FinishUpgrade()
                        | _ -> ()
                        
                | None -> 
                    loop <- false
            | (Binary, data, true) ->
                printf "Binary !"
                ()
            | (Opcode.Close, _, _) ->
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Opcode.Close emptyResponse true
                loop <- false

            | _ -> ()
    }

    let handle: WebPart = fun ctx ->
        let engineCtx = getContext ctx.request
        match ctx.request.``method``, engineCtx.Transport with
        | POST, Some(Polling) ->
            handlePost engineCtx ctx.request |> errorsToHttp |> returnResponse ctx |> Async.result
        | GET, Some(Polling) ->
            let engineCtx = getContext ctx.request
            async {
                let! result = handleGet engineCtx |> Async.ofAsyncResult
                return returnResponse ctx (errorsToHttp result)
            }
        | GET, Some(Websocket) ->
            handShake (handleWebsocket engineCtx) ctx
        | _ ->
            log.info (eventX "???")
            Async.result None

    member val Handle = handle

    member __.Broadcast(messages: PacketContent seq) =
        let messages' = List.ofSeq messages
        sessions |> Map.iter (fun _ socket -> socket.Send(messages'))

    member __.Broadcast(message: PacketContent) =
        sessions |> Map.iter (fun _ socket -> socket.Send(message))

// Fixed in next Suave version, https://github.com/SuaveIO/suave/pull/575
let private removeBuggyCorsHeader: WebPart =
    fun ctx -> async {
        let finalHeaders =
            ctx.response.headers
            |> List.filter (fun (k,v) -> k <> "Access-Control-Allow-Credentials" || v = "True")
            |> List.map(fun (k,v) -> if k = "Access-Control-Allow-Credentials" then k,v.ToLower() else k,v)
        return Some({ ctx with response = { ctx.response with headers = finalHeaders } })
    }

// Adapted from https://github.com/socketio/socket.io/pull/1333
let private disableXSSProtectionForIE: WebPart =
    warbler (fun ctx ->
        let ua = ctx |> Headers.getFirstHeader "User-Agent"
        match ua with
        | Some(ua) when ua.Contains(";MSIE") || ua.Contains("Trident/") ->
            Writers.setHeader "X-XSS-Protection" "0"
        | _ -> succeed
    )

let suaveEngineIo (engine: EngineIo) config: WebPart =
    let corsConfig =
        { defaultCORSConfig with
            allowedMethods = InclusiveOption.None
            allowedUris = InclusiveOption.All
            allowCookies = true }
    choose [
        Filters.pathStarts config.Path
            >=>
            choose [
                engine.Handle
                RequestErrors.BAD_REQUEST "O_o"
            ]
            >=> cors corsConfig
            >=> removeBuggyCorsHeader
            >=> disableXSSProtectionForIE
    ]