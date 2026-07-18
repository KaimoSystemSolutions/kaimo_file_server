# Phase-0-Spike, Schritt A: Stock-Samba mit dynamischen Registry-Shares.
# Ziel: beweisen, dass `net conf` Shares LIVE ohne Reload bereitstellt und
# ein SMB3-Client lesen/schreiben kann. (Custom-VFS-Modul kommt in Schritt B.)
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
