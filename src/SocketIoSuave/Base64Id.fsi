module SocketIoSuave.Base64Id

open System.Security.Cryptography

/// A function that generate one new ID per call
type IdGenerator = unit -> string

/// Create a new ID generator using the specified RNG
val create: RandomNumberGenerator -> IdGenerator