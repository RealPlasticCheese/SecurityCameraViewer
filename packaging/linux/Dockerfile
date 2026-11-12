FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends ruby ruby-dev build-essential rpm python3 ca-certificates && gem install --no-document fpm -v 1.17.0 && rm -rf /var/lib/apt/lists/*
