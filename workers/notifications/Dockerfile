# syntax=docker/dockerfile:1
FROM golang:1.25-alpine AS build-stage

# to supports cgo as sqlite/driver dependency
RUN apk add --update gcc musl-dev

WORKDIR /app

COPY go.mod /app

RUN go mod download

COPY . /app

RUN go build -o app



FROM alpine:3.22 AS deploy-stage

ENV ENVIRONMENT=production

# act as doc only
EXPOSE 8010
LABEL vendor=wastingnotime.org

# principle of least privilege
RUN addgroup -S nonroot && adduser -S appuser -G nonroot

# just to allow appuser to have access to the data volume
WORKDIR /data
RUN chown appuser .

VOLUME /data

WORKDIR /app
RUN chown appuser .

COPY --from=build-stage /app/app .

USER appuser

ENTRYPOINT ["./app"]
