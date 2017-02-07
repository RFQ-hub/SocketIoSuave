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
        getHandshakeSid: HttpContext -> Async<string>
        onPacket: HttpContext -> PacketMessage -> Async<unit>
        getPacket: HttpContext -> Async<PacketMessage option>
        getPayload: HttpContext -> Async<Payload option>
    }

    with static member empty = {
            Upgrades = Array.empty
            CORSConfig =
                { defaultCORSConfig with
                    allowedMethods = InclusiveOption.Some([HttpMethod.GET; HttpMethod.POST])
                    allowedUris = InclusiveOption.All }
            PingTimeout = TimeSpan.FromSeconds(60.)
            PingInterval = TimeSpan.FromSeconds(25.)
            getHandshakeSid = (fun _ -> async { return Guid.NewGuid().ToString() })
            onPacket = (fun _ _ -> async { return () })
            getPacket = (fun _ -> async { return None })
            getPayload = (fun _ -> async { return None })
        }

(*
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EngineIoConfig =
    let empty =
    *)

let okJson x : WebPart = Writers.setMimeType "application/json" >=> Successful.OK x

let parseIntChoice s =
    let ok, i = Int32.TryParse(s)
    if ok then Choice1Of2 i else Choice2Of2 (sprintf "Not a number: '%s'" s)

type EngineIoContext = 
    {
        Transport: string
        JsonPIndex: int option
        SessionId: string
        Base64: bool
    }

    with static member FromHttp (ctx: HttpContext) = {
            Transport = defaultArg (ctx.request.queryParam "transport" |> Option.ofChoice) "polling"
            JsonPIndex = ctx.request.queryParam "j" |> Choice.bind parseIntChoice |> Option.ofChoice
            SessionId = defaultArg (ctx.request.queryParam "sid" |> Option.ofChoice) ""
            Base64 = (defaultArg (ctx.request.queryParam "b64" |> Choice.bind parseIntChoice |> Option.ofChoice) 0) = 1
        }

let serveSocketIo (config: EngineIoConfig) =
    let respondPayload payload engineContext = 
        if engineContext.Base64 then
            Successful.OK (payload |> Payload.encodeToString)
        else
            Successful.ok (payload |> Payload.encodeToBinary |> Segment.toArray)

    let handshakePart ctx = async {
        let! sid = config.getHandshakeSid ctx
        let handshake = {
            Sid = sid;
            Upgrades = config.Upgrades;
            PingTimeout = int config.PingTimeout.TotalMilliseconds;
            PingInterval = int config.PingInterval.TotalMilliseconds }
        let payload = Payload([Open(handshake)])
        let engineContext = EngineIoContext.FromHttp ctx
        let ctx = { ctx with response = { ctx.response with headers = (ctx.response.headers |> List.map(fun (k,v) -> if k = "Access-Control-Allow-Credentials" then k,v.ToLower() else k,v)) } }
        return! respondPayload payload engineContext ctx
    }
        
    cors config.CORSConfig >=> choose [
        handshakePart
    ]

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
