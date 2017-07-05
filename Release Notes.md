### New in 0.3

* Add the `HttpContext` that started the socket.io call in the handler

### New in 0.2

* Remove CORS Specific headers
* Fix bug where connection was dropped when no message was transmitted
* Correctly handle errors from the engine.io layer in the socket.io layer, closing the socket.

### New in 0.1

Initial open source version
