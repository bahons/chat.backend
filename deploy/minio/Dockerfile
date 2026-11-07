FROM golang:1.25-alpine AS build

RUN CGO_ENABLED=0 go install -ldflags="-s -w" github.com/minio/minio@RELEASE.2025-10-15T17-29-55Z

FROM alpine:3.23
COPY --from=build /go/bin/minio /usr/local/bin/minio
ENTRYPOINT ["minio"]
