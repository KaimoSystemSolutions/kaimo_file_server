# Phase-0-Spike, step A: Stock Samba with dynamic registry shares.
# Goal: prove that `net conf` provides shares LIVE without reload and
# an SMB3 client can read/write. (Custom VFS module comes in step B.)
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        samba \
        samba-common-bin \
        smbclient \
    && rm -rf /var/lib/apt/lists/*

COPY conf/smb.conf /etc/samba/smb.conf
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY selftest.sh   /usr/local/bin/selftest.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/selftest.sh

EXPOSE 445

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
