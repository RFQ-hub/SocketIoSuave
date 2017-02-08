open System
open Suave
open Suave.CORS
open Suave.Operators
open SocketIoSuave.EngineIo.Protocol
open SocketIoSuave

let handlePacket (packet: PacketMessage) =
    printfn "Received: %A" packet

type EngineIoConfig =
    {
        Upgrades: string[] // "websocket"
        CORSConfig: CORSConfig
        PingTimeout: TimeSpan
        PingInterval: TimeSpan
        CookieName: string option
        CookiePath: string option
        CookieHttpOnly: bool
        getHandshakeSid: HttpContext -> Async<string>
        onPacket: HttpContext -> PacketMessage -> Async<unit>
        getPacket: HttpContext -> Async<PacketMessage option>
        getPayload: HttpContext -> Async<Payload option>
    }

    with static member empty = {
            CookieName = Some "io"
            CookiePath = Some "/"
            CookieHttpOnly = false
            Upgrades = Array.empty
            CORSConfig =
                { defaultCORSConfig with
                    allowedMethods = InclusiveOption.Some([HttpMethod.GET; HttpMethod.POST])
                    allowedUris = InclusiveOption.All
                    allowCookies = false }
            PingTimeout = TimeSpan.FromSeconds(60.)
            PingInterval = TimeSpan.FromSeconds(25.)
            getHandshakeSid = (fun _ -> async { return Guid.NewGuid().ToString() })
            onPacket = (fun _ _ -> async { return () })
            getPacket = (fun _ -> async { return None })
            getPayload = (fun _ -> async { return None })
        }

let okJson x : WebPart = Writers.setMimeType "application/json" >=> Successful.OK x

module Choice =
    /// Return the value if it's a Choice1Of2 or default' otherwise
    let defaultArg default' = function | Choice1Of2 x -> x | Choice2Of2 _ -> default'

    let parseInt s =
        let ok, i = Int32.TryParse(s)
        if ok then Choice1Of2 i else Choice2Of2 (sprintf "Not a number: '%s'" s)

    let parseIntAsBool s = parseInt s |> Choice.map((<>) 0)

type EngineIoContext = 
    {
        Transport: string
        JsonPIndex: int option
        SessionId: string
        SupportsBinary: bool
        IsBinary: bool
    }

    with static member FromHttp (ctx: HttpContext) = {
            Transport = ctx.request.queryParam "transport" |> Choice.defaultArg "polling"
            JsonPIndex = ctx.request.queryParam "j" |> Choice.bind Choice.parseInt |> Option.ofChoice
            SessionId = ctx.request.queryParam "sid" |> Choice.defaultArg ""
            SupportsBinary = ctx.request.queryParam "b64" |> Choice.bind Choice.parseIntAsBool |> Choice.defaultArg false
            IsBinary = defaultArg (ctx |> Headers.getFirstHeader "content-type") "" = "application/octet-stream"
        }

// Fixed in next Suave version, https://github.com/SuaveIO/suave/pull/575
let removeBuggyCorsHeader: WebPart =
    fun ctx -> async {
        let finalHeaders =
            ctx.response.headers
            |> List.filter (fun (k,v) -> k <> "Access-Control-Allow-Credentials" || v = "True")
            |> List.map(fun (k,v) -> if k = "Access-Control-Allow-Credentials" then k,v.ToLower() else k,v)
        return Some({ ctx with response = { ctx.response with headers = finalHeaders } })
    }


// Adapted from https://github.com/socketio/socket.io/pull/1333
let disableXSSProtectionForIE: WebPart =
    warbler (fun ctx ->
        let ua = ctx |> Headers.getFirstHeader "User-Agent"
        match ua with
        | Some(ua) when ua.Contains(";MSIE") || ua.Contains("Trident/") ->
            Writers.setHeader "X-XSS-Protection" "0"
        | _ -> succeed
    )

let setIoCookie config value : WebPart =
    warbler(fun _ ->
        match config.CookieName with
        | Some(cookieName) ->
            let cookie =
                { HttpCookie.empty with
                    name = cookieName
                    value = value
                    path = config.CookiePath
                    httpOnly = match config.CookiePath with | Some _ -> config.CookieHttpOnly | None -> false
                }
            Cookie.setCookie cookie
        | None -> 
            succeed
    )
    

let serveSocketIo (config: EngineIoConfig) =
    let respondPayload payload engineContext: WebPart = 
        if engineContext.SupportsBinary then
            Writers.setHeader "Content-Type" "text/plain; charset=UTF-8"
                >=> Successful.OK (payload |> Payload.encodeToString)
        else
            Writers.setHeader "Content-Type" "application/octet-stream"
                >=> Successful.ok (payload |> Payload.encodeToBinary |> Segment.toArray)

    let handshakePart: WebPart = fun ctx -> async {
        let! sid = config.getHandshakeSid ctx
        let handshake = {
            Sid = sid;
            Upgrades = config.Upgrades;
            PingTimeout = int config.PingTimeout.TotalMilliseconds;
            PingInterval = int config.PingInterval.TotalMilliseconds }
        let payload = Payload([Open(handshake)])
        let engineContext = EngineIoContext.FromHttp ctx
        let pipeline = setIoCookie config sid >=> respondPayload payload engineContext
        return! pipeline ctx
    }
        
    choose [ handshakePart ]
        >=> cors config.CORSConfig
        >=> removeBuggyCorsHeader
        >=> disableXSSProtectionForIE

[<EntryPoint>]
let main argv = 
    let conf = { EngineIoConfig.empty with Upgrades = Array.empty }
    let app =
        choose [
            Filters.pathStarts "/socket.io/" >=> (serveSocketIo EngineIoConfig.empty)
            Successful.OK "Hello World!"
        ]
    startWebServer defaultConfig app
    0
