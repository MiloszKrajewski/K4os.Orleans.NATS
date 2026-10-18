When using NATS client follow this rules:

* Prefer using small, well-named methods
* Extract identifiable and easy to name pieces of code
* Try not to use methods longer than 20 lines
* Encapsulate communication with other components in little private helper methods exposing only arguments which may change in given class context
* Creation of objects (kv stores, streams, object stores, etc) should always embrace two step pattern:
    1. Try to create or update object (store, stream, etc)
    2. Try to get object if creation failed with `NatsJSApiException` and `ApiError` 500
