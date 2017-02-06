// Learn more about F# at http://fsharp.org
// See the 'F# Tutorial' project for more help.

open Expecto
open SocketIoSuave
open SocketIoSuave.EngineIo.Protocol

let private testEncodeToString message expected format =
    Expect.equal (message |> PacketMessage.encodeToString) expected format

[<Tests>]
let tests =
    testList "encode" [
        testList "string encoding" [
            testCase "open" <| fun _ ->
                let handshake = { Sid= "xxx";Upgrades=[|"foo";"bar"|];PingTimeout=42; PingInterval=43 }
                testEncodeToString (Open(handshake)) @"0{""sid"":""xxx"",""upgrades"":[""foo"",""bar""],""pingTimeout"":42,""pingInterval"":43}" "open"

            testCase "close" <| fun _ ->
                testEncodeToString (Close) "1" "close"

            testCase "ping" <| fun _ ->
                testEncodeToString (Ping(String("probe"))) "2probe" "ping"

            testCase "ping (binary)" <| fun _ ->
                testEncodeToString (Ping(Binary([|1uy;2uy;3uy|] |> Segment.ofArray))) "b2AQID" "ping (binary)"

            testCase "pong" <| fun _ ->
                testEncodeToString (Pong(String("probe"))) "3probe" "pong"

            testCase "pong (binary)" <| fun _ ->
                testEncodeToString (Pong(Binary([|1uy;2uy;3uy|] |> Segment.ofArray))) "b3AQID" "pong (binary)"

            testCase "message" <| fun _ ->
                testEncodeToString (Message(String("Hello world"))) "4Hello world" "message"

            testCase "message (binary)" <| fun _ ->
                testEncodeToString (Message(Binary([|1uy;2uy;3uy|] |> Segment.ofArray))) "b4AQID" "message (binary)"

            testCase "close" <| fun _ ->
                testEncodeToString (Upgrade) "5" "close"

            testCase "noop" <| fun _ ->
                testEncodeToString (Noop) "6" "noop"
        ]
    ]

[<EntryPoint>]
let main args = 
    runTestsInAssembly defaultConfig args