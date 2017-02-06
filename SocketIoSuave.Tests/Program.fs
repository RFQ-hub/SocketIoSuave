// Learn more about F# at http://fsharp.org
// See the 'F# Tutorial' project for more help.

open Expecto
open Experiments

[<Tests>]
let tests =
    testList "encode" [
        testList "string encoding" [
            testCase "open" <| fun _ ->
                Expect.equal (Open.EncodeToString()) "0" "open"

            testCase "close" <| fun _ ->
                Expect.equal (Close.EncodeToString()) "1" "close"

            testCase "ping" <| fun _ ->
                Expect.equal (Ping(String("probe")).EncodeToString()) "2probe" "ping"

            testCase "ping (binary)" <| fun _ ->
                Expect.equal (Ping(Binary([|1uy;2uy;3uy|] |> Segment.ofArray)).EncodeToString()) "b2AQID" "ping (binary)"

            testCase "pong" <| fun _ ->
                Expect.equal (Pong(String("probe")).EncodeToString()) "3probe" "pong"

            testCase "pong (binary)" <| fun _ ->
                Expect.equal (Pong(Binary([|1uy;2uy;3uy|] |> Segment.ofArray)).EncodeToString()) "b3AQID" "pong (binary)"

            testCase "message" <| fun _ ->
                Expect.equal (Message(String("Hello world")).EncodeToString()) "4Hello world" "message"

            testCase "message (binary)" <| fun _ ->
                Expect.equal (Message(Binary([|1uy;2uy;3uy|] |> Segment.ofArray)).EncodeToString()) "b4AQID" "message (binary)"

            testCase "close" <| fun _ ->
                Expect.equal (Upgrade.EncodeToString()) "5" "close"

            testCase "noop" <| fun _ ->
                Expect.equal (Noop.EncodeToString()) "6" "noop"
        ]
    ]

[<EntryPoint>]
let main args = 
    runTestsInAssembly defaultConfig args