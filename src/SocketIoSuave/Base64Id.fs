/// Generate Base64 unique IDs similar to socket.io
module SocketIoSuave.Base64Id

open System
open System.Security.Cryptography
open System.Threading

type IdGenerator = unit -> string

let create (rng: RandomNumberGenerator): IdGenerator =
    let buffer = new ThreadLocal<byte[]>(fun () -> Array.zeroCreate(15))
    let mutable sequenceNumber = 0
    fun () ->
        rng.GetBytes(buffer.Value)
        let sequenceNumber = Interlocked.Increment(&sequenceNumber)
        buffer.Value.[11] <- byte sequenceNumber
        buffer.Value.[12] <- byte (sequenceNumber >>> 8)
        buffer.Value.[13] <- byte (sequenceNumber >>> 16)
        buffer.Value.[14] <- byte (sequenceNumber >>> 24)
        Convert.ToBase64String(buffer.Value)