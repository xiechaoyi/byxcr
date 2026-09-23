# syntax=docker/dockerfile:1

######## Build Source ########
FROM ccr.ccs.tencentyun.com/byxcr/dotnet:10.0-sdk-alpine AS publish

# 刻意不传 -r：publish 会按当前基础镜像的架构自动选 RID（alpine 下即 linux-musl-x64 / linux-musl-arm64）。
# 需要 arm64 产物时用 buildx 跨平台构建，让它拉取对应架构的基础镜像（SDK 与目标架构一致）：
#   docker buildx build --platform linux/arm64 -t byxcr:arm64 .
# 不要改成传 --build-arg RID=…：x64 的 SDK 编译不出可用的 arm64 NativeAOT 产物。
COPY --chmod=0777 ./ /code
RUN cd /code && dotnet publish byxcr.csproj -o /dist -c Release -p:PublishAot=true -p:PublishTrimmed=true
RUN rm -f /dist/*.dbg

######## Build Images ########
FROM ccr.ccs.tencentyun.com/byxcr/aot:10.0-alpine AS builder

# NativeAOT 产物：byxcr 可执行文件 + 同目录的 libe_sqlite3.so
COPY --chmod=0777 --from=publish /dist /wln

# 访问 HTTPS 镜像源需要根证书；gzip 用于外部压缩快路径（缺失时自动回退内置实现）；
# tzdata 是 TZ=Asia/Shanghai 生效的前提（musl 精简镜像默认没有 zoneinfo，缺了就只能按 UTC 显示）。
RUN apk add --no-cache ca-certificates gzip tzdata && /wln/byxcr init

ENV BYXCR_REGISTRIES="mirror.ccs.tencentyun.com,docker.1ms.run" \
    BYXCR_IGNORE_ARCHIVE_CHECK=true \
    BYXCR_CONFIG=/wln/data/byxcr.yaml \
    BYXCR__Storage__DataDir=/wln/data \
    BYXCR__Log__Color=false \
    TZ=Asia/Shanghai \
    PATH=/wln:${PATH}

# 数据库 + 归档目录，建议挂载出去
VOLUME ["/wln/data", "/wln/images"]
WORKDIR /wln
ENTRYPOINT ["/wln/byxcr"]
CMD ["run"]
