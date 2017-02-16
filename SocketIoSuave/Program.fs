open System
open Suave
open Suave.CORS
open Suave.Operators
open SocketIoSuave.EngineIo.Protocol
open SocketIoSuave
open System.Security.Cryptography
open Suave.Logging
open Suave.Logging.Message
open SocketIoSuave.EngineIo
open SocketIoSuave.SocketIo
open SocketIoSuave.SocketIo.Engine
open System.Collections.Generic
open System.Threading.Tasks

let handlePacket (packet: PacketMessage) =
    printfn "Received: %A" packet

let okJson x : WebPart = Writers.setMimeType "application/json" >=> Successful.OK x

let private log = Targets.create Debug [| "SocketIoSuave" |]

[<EntryPoint>]
let main argv = 
    let socketio handlePackets = SocketIo(handlePackets).WebPart

    let rec handlePacket (socket: ISocketIoSocket) = async {
        let logVerbose s = log.debug (eventX (sprintf "program {socketId}: %s" s) >> setField "socketId" (id.ToString()))
        let! p = socket.Receive()
        match p with
        |Some packet ->
            log.info (eventX (sprintf "< %A" packet))
            match packet.EventId with
            | Some id -> socket.Send({ Packet.ofType Ack with EventId = Some id })
            | None -> ()
            let cmd = packet.Data.[0].ToObject<string>()
            if cmd = "chat message" then
                let textMessage = packet.Data.[1].ToObject<string>()
                logVerbose (sprintf "Received %s" textMessage)
                socket.Broadcast packet
            logVerbose "Let's loop"
            return! handlePacket socket
        |None ->
            logVerbose "None received, it's the end !"
            return ()
    }

    let app =
        choose [
            //serveEngineIo { EngineIoConfig.empty with Path = "/socket.io/"; InitialPackets = initialPackets }
            socketio handlePacket
            // serveSocketIo emptySocketIoConfig
            Successful.OK "Hello World!"
        ]
    let suaveConf = { defaultConfig with logger = log }
    startWebServer suaveConf app
    0
