# Third-party software notices

EDMW Mod Studio v1.4.0 includes the following third-party components for direct WebRTC gameplay. Their licenses remain with their respective authors.

| Component | Version / use | License and project |
|---|---|---|
| DataChannelDotnet | 1.3.1 managed .NET WebRTC wrapper | MIT; [NuGet package](https://www.nuget.org/packages/DataChannelDotnet/1.3.1), [source project](https://github.com/ZetrocDev/DataChannelDotnet) |
| libdatachannel | Native WebRTC data channel runtime in `datachannel.dll` | MPL 2.0; [project and license](https://github.com/paullouisageneau/libdatachannel) |
| libjuice | ICE/STUN implementation used by libdatachannel | MPL 2.0; [project and license](https://github.com/paullouisageneau/libjuice) |
| usrsctp | SCTP transport used by libdatachannel | BSD 3-Clause; [project and license](https://github.com/sctplab/usrsctp/blob/master/LICENSE.md) |
| libsrtp | SRTP support included by the native runtime | BSD 3-Clause; [project and license](https://github.com/cisco/libsrtp/blob/main/LICENSE) |
| plog | Native logging library included by the runtime | MIT; [project and license](https://github.com/SergiusTheBest/plog) |
| OpenSSL | Native `libcrypto-3` and `libssl-3` runtime libraries | Apache 2.0; [project and license](https://github.com/openssl/openssl/blob/master/LICENSE.txt) |

The DataChannelDotnet package metadata identifies its source repository commit as `c4d8a94a083cd2acffe253762afcecb72c079210`. Its NuGet package provides Windows x86 and x64 native runtime binaries. The package contains the managed assembly and native binaries, but not the native source trees. The linked upstream projects provide their license texts; the package metadata is the source reference for the wrapper build.

The application source in this repository does not modify these third-party components. This notice is provided with the source and Windows distribution; consult each linked license for its terms.
