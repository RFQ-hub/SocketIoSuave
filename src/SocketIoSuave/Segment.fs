/// ArraySegment helpers
module internal Segment

open System

let ofArray (arr: 't[]) = new ArraySegment<'t>(arr)

let empty<'a> = Array.empty<'a> |> ofArray

let subset offset count (segment: ArraySegment<'t>) =
    if offset < 0 then raise (ArgumentOutOfRangeException("offset", offset, "offset is negative"))
    if count < 0 then raise (ArgumentOutOfRangeException("count", offset, "count is negative"))
    if offset > segment.Count then raise (ArgumentOutOfRangeException("offset", offset, "offset is out of the segment"))
    if offset + count > segment.Count then raise (ArgumentOutOfRangeException("count", "not enough elements in the segment"))

    new ArraySegment<'t>(segment.Array, segment.Offset + offset, count)

let take count segment = subset 0 count segment

let skip count (segment: ArraySegment<'t>) = subset count (segment.Count - count) segment

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

let valueAt i (segment: ArraySegment<'t>) =
    if i < 0 || i >= segment.Count then
        raise (new System.ArgumentOutOfRangeException("i", "Index is out of the segment"))

    segment.Array.[segment.Offset + i]