#!/bin/bash
podman run --runtime runc --platform linux/amd64 --rm -it -v $(pwd):/code -w /code ccr.ccs.tencentyun.com/byxcr/dotnet:10.0-sdk-alpine \
dotnet publish /code -o /code/fnos/dist/amd64 --self-contained -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:PublishSingleFile=false
podman run --runtime runc --platform linux/arm64 --rm -it -v $(pwd):/code -w /code ccr.ccs.tencentyun.com/byxcr/dotnet:10.0-sdk-alpine \
dotnet publish /code -o /code/fnos/dist/arm64 --self-contained -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:PublishSingleFile=false

rm -f $(pwd)/fnos/dist/amd64/*.dbg
rm -f $(pwd)/fnos/dist/arm64/*.dbg
./fnos/build.sh x86 --bin-dir $(pwd)/fnos/dist/amd64
./fnos/build.sh arm --bin-dir $(pwd)/fnos/dist/arm64