# @NAME@ native ABI

Handles belong to the creating library. Release each handle once with the
exported destroy function. The library never transfers ownership of caller
buffers. Synchronize concurrent access to an individual handle.

For the optional managed consumer, configure an explicit shared native build
and enable the test with an installed .NET 10 SDK.

```sh
cmake --preset release -DBUILD_SHARED_LIBS=ON -DCMWF_INTEROP_MANAGED_TEST=ON
cmake --build --preset release
ctest --preset release
```
