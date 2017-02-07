module SocketIoSuave.SegmentTests

open Expecto

[<Tests>]
let segmentTests =
    testList "segment" [
        testList "subset" [
            testCase "simple total size" <| fun _ ->
                let segment = [|1;2;3;4;5|] |> Segment.ofArray |> Segment.subset 0 5
                Expect.equal segment.Offset 0 "offset"
                Expect.equal segment.Count 5 "count"

            testCase "simple subset" <| fun _ ->
                let segment = [|1;2;3;4;5|] |> Segment.ofArray |> Segment.subset 1 3
                Expect.equal segment.Offset 1 "offset"
                Expect.equal segment.Count 3 "count"

            testCase "complex total size" <| fun _ ->
                let segment = [|1;2;3;4;5;6;7|] |> Segment.ofArray |> Segment.subset 1 5 |> Segment.subset 0 5
                Expect.equal segment.Offset 1 "offset"
                Expect.equal segment.Count 5 "count"

            testCase "complex subset" <| fun _ ->
                let segment = [|1;2;3;4;5;6;7|] |> Segment.ofArray |> Segment.subset 1 5 |> Segment.subset 1 3 
                Expect.equal segment.Offset 2 "offset"
                Expect.equal segment.Count 3 "count"
        ]
    ]