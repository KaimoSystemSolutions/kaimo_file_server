from impacket.smbconnection import SMBConnection
from io import BytesIO

HOST = "127.0.0.1"
PORT = 4445
SHARE = "test"

try:
    conn = SMBConnection(HOST, HOST, sess_port=PORT)
    print(f"[+] Verbindung zu {HOST}:{PORT} erfolgreich")

    conn.login("guest", "")
    print("[+] Guest-Login erfolgreich")

    # Shares auflisten
    shares = conn.listShares()
    print("[+] Verfügbare Shares:")
    for share in shares:
        print(f"    - {share['shi1_netname'][:-1]}")

    # Datei schreiben
    inhalt = b"Hallo vom Python SMB Client!"
    datei = BytesIO(inhalt)
    conn.putFile(SHARE, "\\test.txt", datei.read)
    print("[+] Datei geschrieben: test.txt")

    conn.logoff()
    print("[+] Verbindung sauber getrennt")

except Exception as e:
    print(f"[-] Fehler: {e}")