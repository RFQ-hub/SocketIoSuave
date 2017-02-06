module SocketIoSuave.Segment

open System

let ofArray (arr: 't[]) = new ArraySegment<'t>(arr)

let empty<'a> = Array.empty<'a> |> ofArray

let take count (segment: ArraySegment<'t>) =
    new ArraySegment<'t>(segment.Array, segment.Offset, min count segment.Count)

let totalSize (segments: ArraySegment<'t> seq) =
    segments |> Seq.sumBy (fun s -> s.Count)

let concat (segments: ArraySegment<'t> seq) =
    let count = totalSize segments
    let result = Array.create<'t> count Unchecked.defaultof<'t>
    let mutable i = 0L
    for segment in segments do
        Array.Copy(segment.Array, int64 segment.Offset, result, i, int64 segment.Count)
        i <- i + (int64 segment.Count)
    result

let toArray (segment: ArraySegment<'t>) =
    if segment.Offset = 0 && segment.Count = segment.Array.Length then
        segment.Array
    else
        let result = Array.zeroCreate segment.Count
        Array.Copy(segment.Array, segment.Offset, result, 0, segment.Count)
        result