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

let rec onSocket (socket: EngineIoSocket) = async {
    let! message = socket.Read()
    match message with
    | Some(packet) ->
        printfn "Received: %A" packet
        return! onSocket socket
    | None ->
        return ()
}

let serveSocketIo config =
    suaveEngineIo config.EngineConfig onSocket

[<EntryPoint>]
let main argv = 
    let conf = { EngineIoConfig.empty with Upgrades = Array.empty }
    let app =
        choose [
            //serveEngineIo { EngineIoConfig.empty with Path = "/socket.io/"; InitialPackets = initialPackets }
            serveSocketIo emptySocketIoConfig
            Successful.OK "Hello World!"
        ]
    let suaveConf = { defaultConfig with logger = Targets.create Debug [| "Suave" |] }
    startWebServer suaveConf app
    0
