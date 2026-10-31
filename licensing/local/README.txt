Zentriq License Server - local edition (Windows)
================================================

Runs the license server and its own PostgreSQL from this folder, for testing before the public license server goes
live. Nothing is installed; nothing is reachable from other computers (PostgreSQL 127.0.0.1:5439, server
http://localhost:5080). Everything it creates is in the "data" folder.

1. Unpack the zip to a folder you control, e.g. C:\ZentriqLicense (not Program Files, not a synced OneDrive folder).
2. Open a Command Prompt in that folder and run:   zls setup
     - creates the database
     - KEY CEREMONY: creates the production license signing key ON THIS COMPUTER, encrypted with your passphrase
     - creates your owner account for the license portal
3. Run:   zls start          (or "zls start -Background" to get the prompt back; "zls stop" stops everything)
   Portal:                   http://localhost:5080/admin
   Offline activation page:  http://localhost:5080/

Try the license generation:
   Customers > New customer;  Licenses > New license (subscription 1/3/5 years or perpetual with support) - the
   license key is shown once;  open the license > Offline activation > "issue a test file" (or attach a real
   activation request from a Zentriq server);  then Verify file to inspect what was signed.

The signing key (data\keys):
   <key id>.private.pem        encrypted private key. Back it up to two offline places. NEVER email or upload it.
   <key id>.product-entry.json the public key. Send only this file to Zentriq so the product trusts your licenses.
   <key id>.ceremony.txt       record with the fingerprint; confirm the fingerprint by phone when handing over the
                               public key.
   The passphrase is kept protected for this Windows user (DPAPI) so "zls start" needs no prompt; use
   "zls setup -NoStorePassphrase" to be asked at every start instead.

Testing with a Zentriq server on the SAME computer (after Zentriq has been built with your public key):
   Zentriq > Licensing > License server: http://localhost:5080  ; then Activate online with the license key.

Other commands:  zls status | zls backup (database) | zls key-info
