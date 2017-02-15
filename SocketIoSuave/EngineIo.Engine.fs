module SocketIoSuave.EngineIo.Engine

open SocketIoSuave
open SocketIoSuave.EngineIo.Protocol
open System
open System.Security.Cryptography
open System.Collections.Generic
open Suave.Logging
open Suave.Logging.Message
open Suave
open Suave.CORS
open Suave.Operators
open Suave.Cookie
open Chessie.ErrorHandling
open System.Threading.Tasks

let private log = Log.create "engine.io"

type SocketId = SocketId of string
with
    override x.ToString() = match x with | SocketId s -> s

type EngineIoConfig =
    {
        Path: string
        Upgrades: string[] // "websocket"
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
            Upgrades = Array.empty

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
        Sid = socketId;
        Upgrades = config.Upgrades;
        PingTimeout = int config.PingTimeout.TotalMilliseconds;
        PingInterval = int config.PingInterval.TotalMilliseconds
    }

type Error =
    | UnknownSessionId
    | Unknown

type IEngineIoSocket =
    abstract member Id: SocketId with get
    abstract member Read: unit -> Async<PacketContent option>
    abstract member Send: PacketContent list -> unit
    abstract member Send: PacketContent seq -> unit
    abstract member Send: PacketContent -> unit
    abstract member Close: unit -> unit

type private IncomingCommunication =
    | NewIncomming of PacketMessage
    | ReadIncomming of AsyncReplyChannel<PacketContent option>
    | CloseIncomming

type private OutgoingCommunication =
    | NewOutgoing of PacketMessage list
    | ReadOutgoing of AsyncReplyChannel<Payload>
    | CloseOutgoing

type private EngineIoSocket(id: SocketId, pingTimeout: TimeSpan, onClose: EngineIoSocket -> unit, handleSocket: IEngineIoSocket -> Async<unit>) as this =
    let logVerbose s = log.debug (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logError s = log.error (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logDebug s = log.debug (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    let logWarn s = log.warn (eventX (sprintf "engine {socketId}: %s" s) >> setField "socketId" (id.ToString()))
    
    let closeLock = new obj()
    let mutable closed = false
    let mutable task: Task = null

    let timeoutInMs = int pingTimeout.TotalMilliseconds

    let pingTimeoutTimer =
        let timer = new System.Timers.Timer()
        timer.AutoReset <- false
        timer.Interval <- pingTimeout.TotalMilliseconds
        timer.Elapsed.Add(fun _ ->
            logDebug "Ping timeout, closing socket"
            this.Close())
        timer

    let setPingTimeout () =
        pingTimeoutTimer.Stop()
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
                    logWarn "[NewIncomming] Open received during communication"
                    return! loop messages currentReplyChan
                | Upgrade ->
                    // TODO
                    logWarn "[NewIncomming] Upgrade not handled for now"
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

    let outgoing = MailboxProcessor<OutgoingCommunication>.Start(fun inbox ->
        let rec loop messages (currentReplyChan: AsyncReplyChannel<Payload> option) = async {
            let! msg = inbox.Receive()
            match msg with
            | NewOutgoing newMessages ->
                logVerbose (sprintf "NewOutgoing %A" newMessages)
                let allMessages = List.append newMessages messages
                match currentReplyChan with
                | Some(chan) ->
                    chan.Reply(Payload allMessages)
                    return! loop [] None
                | None ->
                    return! loop allMessages None
            | ReadOutgoing rep ->
                match currentReplyChan with
                | Some otherRep ->
                    // Two requests crossed, if the client behave normally the first one had a network error and
                    // timeouted, but just in case we still answer something
                    logWarn "[ReadOutgoing] Requests crossed, previous will get an empty payload"
                    otherRep.Reply(Payload([]))
                | None -> ()

                match messages with
                | []-> return! loop [] (Some(rep))
                | messages ->
                    rep.Reply(Payload(messages))
                    return! loop [] None
            | CloseOutgoing ->
                logVerbose "CloseOutgoing"
                match currentReplyChan with
                | Some(chan) -> chan.Reply(Payload([Close]))
                | None -> ()
                return ()
        }

        loop [] None
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
                onClose this
                pingTimeoutTimer.Stop()
                pingTimeoutTimer.Dispose()
                incomming.Post CloseIncomming
                outgoing.Post CloseOutgoing
                logDebug "Closed")
    
    interface IEngineIoSocket with
        member __.Id with get () = this.Id
        member __.Read() = this.ReadIncomming()
        member __.Send(messages) = this.AddOutgoing(messages |> List.map Message)
        member __.Send(messages) = this.AddOutgoing(messages |> Seq.map Message |> List.ofSeq)
        member __.Send(message) = this.AddOutgoing([Message message])
        member __.Close() = this.Close()

let inline private badAsync err = Async.result (Bad [err]) |> AR
let inline private okAsync ok = Async.result (Ok(ok,[])) |> AR

type EngineApp = 
    {
        handleSocket: IEngineIoSocket -> Async<unit>
    }

type private RequestContext =
    {
        Transport: string
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
        Transport = req |> queryParam "transport" |> Option.defaultArg "polling"
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
let mutateField<'t when 't: not struct> (targetField: 't byref) (mutation: 't -> 't) =
    let mutable retry = true
    while retry do
        let before = targetField
        let newValue = mutation before
        let afterExchange = System.Threading.Interlocked.CompareExchange(&targetField, newValue, before)
        retry <- not (obj.ReferenceEquals(before, afterExchange))

type EngineIo(config, app: EngineApp) =
    let mutable sessions: Map<SocketId, EngineIoSocket> = Map.empty
    let idGenerator = Base64Id.create config.RandomNumberGenerator
    
    let socketTimeout = config.PingTimeout + config.PingInterval
    
    let socketClosed (socket: EngineIoSocket) =
        log.info (eventX (sprintf "Removing session with ID %s" (socket.Id.ToString())))
        mutateField &sessions (fun s -> s |> Map.remove socket.Id)

    let payloadToResponse sid payload engineCtx =
        if engineCtx.SupportsBinary then
            bytesResponse HttpCode.HTTP_200 (payload |> Payload.encodeToBinary |> Segment.toArray)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "application/octet-stream"
            |> setContentBytes (payload |> Payload.encodeToBinary |> Segment.toArray)
        else
            bytesResponse HttpCode.HTTP_200 (payload |> Payload.encodeToString |> UTF8.bytes)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "text/plain; charset=UTF-8"
            |> setContentBytes (payload |> Payload.encodeToString |> System.Text.Encoding.UTF8.GetBytes)

    let handleGet' engineCtx: AsyncResult<SocketId*Payload, Error> =
        match engineCtx.SessionId with
        | None ->
            let socketIdString = idGenerator ()
            let socketId = SocketId socketIdString

            let socket = new EngineIoSocket(socketId, socketTimeout, socketClosed, app.handleSocket)
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
        match engineCtx.SessionId with
        | None -> fail UnknownSessionId
        | Some(sessionId) -> 
            let socketId = SocketId sessionId
            match sessions |> Map.tryFind socketId with
            | Some(socket) ->
                let payload =
                    if engineCtx.SupportsBinary then
                        req.rawForm |> Segment.ofArray |> Payload.decodeFromBinary
                    else
                        req.rawForm |> Text.Encoding.UTF8.GetString |> Payload.decodeFromString
                for message in payload |> Payload.getMessages do
                    log.debug (eventX (sprintf "%s -> %A" sessionId message))
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

    let handle: WebPart = fun ctx ->
        let engineCtx = getContext ctx.request

        match ctx.request.``method`` with
        | POST -> handlePost engineCtx ctx.request |> errorsToHttp |> returnResponse ctx |> Async.result
        | GET -> async {
            let! result = handleGet engineCtx |> Async.ofAsyncResult
            return returnResponse ctx (errorsToHttp result)
            }
        | _ -> Async.result None

    member val Handle = handle


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