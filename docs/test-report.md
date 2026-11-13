# Test report

Latest: GitHub Actions run 43469315922, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM set to India Standard Time plus an Ubuntu job for the Linux agent. Release:
`v1.1.1-build.141` (pre-release, **unsigned**), package version **1.1.1.141**, commit `097b27c`. Results: unit +
PostgreSQL integration tests **508 PASS** (510, 2 skipped); installer acceptance **93 passed, 0 failed** (10 not run:
need hardware, a domain, a reboot or old Windows); upgrade suites **14/14** (schema 45); Linux agent on 11
distributions **11/11**.

New in build 141: help text in the console has its own look for each kind of thing — numbered steps with a title,
menu paths as chips, button names, values and file names, commands in a terminal box with a Copy button, tips and
warnings — instead of bold text everywhere. Build 140 (same product code) was not released: acceptance T54 waited
only 4 minutes for an alert that the busy worker sent after 4 min 15 s; the test now waits up to 8 minutes.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `8a17ad33052a4bdf4f368371738f6b9e63b496a6847b9c49b0356fe8c16631fc` |
| Zentriq.msi | `39dabdda8a91319fedce2a7ba3090c67bde7c7b97800e9c8dabec5874a2dc8ef` |
| ZentriqAgent-x64.msi | `c60e8ffdd5acc86b014895466e919befca3659e5ef48014d80240a31e5fd7a9b` |
| zentriq-agent-linux-x64.deb | `30dfb4e6ccd56e8054a755e0db7ff371eec0301e743a72e132e002dece922356` |
| zentriq-agent-linux-x64.rpm | `16f326194b383adb5c2af47ab7601ecc66349e05f93478b4ee2b667e36cf35a9` |
| zentriq-agent-linux-x64.tar.gz | `a997992e6a6029be6080eec614553f7e5543dbc28ac614eb5c030384075bb128` |
| zentriq-agent-linux-arm64.deb | `be4420e00eccbcb6df6b0132adf4014122ebc2ae4b9b0cf690e4581f5006d232` |
| zentriq-agent-linux-arm64.rpm | `689f5c21df8afde941ead8f576fe451c850e100d1dabff07948d9858424da13e` |
| zentriq-agent-linux-arm64.tar.gz | `04a7b1af5f18d31c4c5b3e250c9795b818b8d3e27680f2c2ecefa7f0db664528` |
| Zentriq-1.1.1-msi-offline-kit.zip | `24d720c65e784c08f00cb0cfa036e3e7c753200edc17fa6ce9203e8e30ae68c4` |
| Zentriq-1.1.1-docs.zip | `042dad2ada184de628e525f3f938e045b87a810dd86754947c81509c185e0d9f` |

## Build 139

GitHub Actions run 43434856768, commit `aa048df`, release `v1.1.1-build.139`: unit + integration **508 PASS**,
acceptance **93 passed, 0 failed**, upgrades **14/14**, Linux **11/11**. Sign-in page: the Zentriq logo above the
organization's logo; the Zentriq / directory account switch centred.

## Build 138

GitHub Actions run 43396329750, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM set to India Standard Time plus an Ubuntu job for the Linux agent. Release:
`v1.1.1-build.138` (pre-release, **unsigned**), package version **1.1.1.138**, commit `0836fda`. Results: unit +
PostgreSQL integration tests **508 PASS** (510, 2 skipped); installer acceptance **93 passed, 0 failed** (10 not run:
need hardware, a domain, a reboot or old Windows); upgrade suites **14/14** (schema 45); Linux agent on 11
distributions **11/11**.

New in build 138: sign-out after inactivity (Settings › Security, 0-720 minutes, default 30): typing, clicking or
scrolling in any console tab is activity, pages refreshing on their own are not; warning one minute before; enforced by
the server from the idle time each request reports (T102).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `dd6bc652817a844bc0750af67226de5fcafe5f071d7e1c223647064dab90de46` |
| Zentriq.msi | `e96b087f75b115a9b72d34fef263b4ed8c50619635a6f9360c514dfd5535fa48` |
| ZentriqAgent-x64.msi | `67a9e03b0cac22999233ec179e8074e0202f10d6b529457f07df4d6e66beab72` |
| zentriq-agent-linux-x64.deb | `d31e1860e72359f97fe81f01d8da45382ba41d8b936c8734c4be8ddbc591ffad` |
| zentriq-agent-linux-x64.rpm | `f86a52e610ad3b2c2710e9a13537a1bd8ada6ea287545b7f17c0abf542b966bb` |
| zentriq-agent-linux-x64.tar.gz | `509e34224cec82f14c89dc003d58f4b3b537cb90d44a21eab5bdbda1711f3b22` |
| zentriq-agent-linux-arm64.deb | `49520f42b52ee81ac20e286a2f00d1099be8777e73e7f408557291cef2d1a10e` |
| zentriq-agent-linux-arm64.rpm | `573da9c28a96b6b8f68482d7b00c0e371c19a009e812ff2c6391a94638338574` |
| zentriq-agent-linux-arm64.tar.gz | `26a196848b3fe221567157348c9d3110080e63f2a0959bc714c6e6a0520edd6f` |
| Zentriq-1.1.1-msi-offline-kit.zip | `99a7788cfb3171fa96047f82c848d5297cccd380addfd476a8adb9aca38d80ed` |
| Zentriq-1.1.1-docs.zip | `df316d63991c9fc9c8cc48e0304cf87714e331a366de73d0e7fb52c318b3a617` |

## Build 137

GitHub Actions run 43375071807, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM set to India Standard Time plus an Ubuntu job for the Linux agent. Release:
`v1.1.1-build.137` (pre-release, **unsigned**), package version **1.1.1.137**, commit `395c2ed`. Results: unit +
PostgreSQL integration tests **492 PASS** (494, 2 skipped); installer acceptance **92 passed, 0 failed** (10 not run:
need hardware, a domain, a reboot or old Windows); upgrade suites **14/14** (schema 44); Linux agent on 11
distributions **11/11**.

New in build 137: the agent list has a search box, 50 agents per page, and actions on selected agents (Update,
Collect inventory, Schedule); the Active Directory / LDAP connection test names the cause when it fails (port closed,
LDAPS chosen on plain-LDAP port 389, no LDAPS certificate on the domain controller, untrusted certificate, name
mismatch, wrong user name or password); saving refuses LDAPS on 389/3268 and LDAP on 636/3269.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `39a0f96e7e4b99abefd9bd6e849b54f2b96cf04a21d9933a0bdb14924537034b` |
| Zentriq.msi | `0c96b23164628daff6c210c57cf5b925ffdb3cf33282023097d98bba3fe73507` |
| ZentriqAgent-x64.msi | `a2564ff3ad0d55b1c75d5162ba16836c14f4c0e8cc0f5069f06a80aef44c62cb` |
| zentriq-agent-linux-x64.deb | `f7e794b71925c466ca1411df6aa55dfedbad0e04eb68fd93970f54ad2f033fb3` |
| zentriq-agent-linux-x64.rpm | `9802800a636491e9a9035af02a12df04f8b73b663119557e47636e4b6b6a8acf` |
| zentriq-agent-linux-x64.tar.gz | `240fc6b76b26fbe4af6e10649b1894e2ab42233c1850da703ea48e347b6a2d66` |
| zentriq-agent-linux-arm64.deb | `d6af54f85bb75204927b7648ece52b413e21f04adfc1cac961e4f97a6dc0bc30` |
| zentriq-agent-linux-arm64.rpm | `7f1ce57b607ee1d082ee6a949cfc3cd82a3a5f099873b727af8e39d28aa492cc` |
| zentriq-agent-linux-arm64.tar.gz | `d3ce3d9278df5306084b17b1c1d7e88dd318ec20b6f7a2dd56272b4fbc8fef9c` |
| Zentriq-1.1.1-msi-offline-kit.zip | `6a25234b3ce0a21f72736ee7cd4437b12b3fff990cdf083f71f39057dc628d6d` |
| Zentriq-1.1.1-docs.zip | `3773fa88d58f1d70c2a19d4439ddb8f32fdc9fc48c486cd1036c93fff415ef15` |

## Build 136

GitHub Actions run 43266189157, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM set to India Standard Time plus an Ubuntu job for the Linux agent. Release:
`v1.1.1-build.136` (pre-release, **unsigned**), package version **1.1.1.136**, commit `b22364a`. Results: unit +
PostgreSQL integration tests **492 PASS** (494, 2 skipped); installer acceptance **92 passed, 0 failed** (10 not run:
need hardware, a domain, a reboot or old Windows); upgrade suites **14/14** (schema 44); Linux agent on 11
distributions **11/11**.

New in build 136: data scope per role and group (locations, departments, device groups; PostgreSQL row-level security
for scoped users' connections only), access rules per user (expiry, sign-in hours, allowed networks), MFA required per
role, directory groups mapped to Zentriq groups (T101); Patch Management splits Windows & OS updates from software
updates (Microsoft software from Windows Update / WSUS, third-party applications through winget — the CI runner,
Windows Server 2022, has no winget and the scan says so); renamed computers recorded in the change history.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `65d127bb8d9c263c91d48795c3391baf58bf01c24e41040d95b0772ac1ff3909` |
| Zentriq.msi | `2e83160ef69be684140c20663671dc32612e015309938f9015b7b12fec430399` |
| ZentriqAgent-x64.msi | `41f64c05498594292c16a7b7e21064deabbe26382d4bf1cd3c40443d9bc1cd49` |
| zentriq-agent-linux-x64.deb | `c07a9f882849312b990b927890bf0b6aedb7fd00b915c01ddeacbeb805838144` |
| zentriq-agent-linux-x64.rpm | `090c92f7eef41cf70a9694327b1ca1e3c4e338d1f4a09be5932a34bacd0f070a` |
| zentriq-agent-linux-x64.tar.gz | `ff4d02d670fc58f96a7d421cb47ce96bdf58265b8753654adbd9cfb8576e47aa` |
| zentriq-agent-linux-arm64.deb | `9ede18a6e91510c3e15da3fc9ca4447a6bd676cbd8612b0a44ed21dd22fd0487` |
| zentriq-agent-linux-arm64.rpm | `70505f7444e9ae3bf575e418630072471eb037754b980df680fa856db38e6b80` |
| zentriq-agent-linux-arm64.tar.gz | `30e8b64f07add2479339d0761b85a1d9f30d8d6c111b9177f1faa3f4a0f3811a` |
| Zentriq-1.1.1-msi-offline-kit.zip | `c5108b325ecab0191478c2e8ea1cdd42443135321dc8e656fdd4e1db3b6554a7` |
| Zentriq-1.1.1-docs.zip | `5d74ace66097cb5d813b82138d292a76ee1dfc61e7bb37e0b8e2844f465ac3b0` |

## Build 135

GitHub Actions run 43235973527, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM **set to India Standard Time (UTC+05:30)** plus an Ubuntu job for the Linux agent. Release:
`v1.1.1-build.135` (pre-release, **unsigned**), package version **1.1.1.135**, commit `0dd9b6e`. Results: unit +
PostgreSQL integration tests **487 PASS** (489, 2 skipped); installer acceptance **91 passed, 0 failed** (10 not run:
need hardware, a domain, a reboot or old Windows); upgrade suites **14/14** (schema 42); Linux agent on 11
distributions **11/11**. No "Offset=05:30" error in any server log.

New in build 135: on servers outside UTC, times reach PostgreSQL as UTC — Active Directory sync and the AD computer
scan (lastLogonTimestamp, pwdLastSet: "Cannot write DateTimeOffset with Offset=05:30:00"), report and backup schedules,
scheduled scripts, Hyper-V dates, and every time in API and agent JSON. The AD panel says how a sync went when it
finishes. Emptying a search box shows all rows again without pressing Search (24 lists). MeshCentral 1.2.5 runs with
patched `node-forge` 1.4.3, `compression` 1.8.2 and a bundled `image-size` 2.1.0: no high-severity findings left
(9 moderate in `glob`/`minimatch`). The release acceptance suite now runs in India Standard Time.

Run 43219999387 (build 134) was not released: T48 failed (90 passed, 1 failed) — saving a report schedule returned
500 in India Standard Time (next run computed with the local offset). Fixed with the backup schedule's same pattern.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `7f6edc7a57920ab77a11e9095bcc4a236d63efcd3a2464ba2f399caafbf6b473` |
| Zentriq.msi | `fad05fc08d19798ae868bff983200134686e45f8460bcd520afaafad2b4dc9cc` |
| ZentriqAgent-x64.msi | `452a1d980b9d1396a54480dcb2b6b5ac23f42bdba1c8b13b0505fba86fb468f0` |
| zentriq-agent-linux-x64.deb | `745097f6c118fca86009a38075fb17fd786c5d99525352a3f93e4bb8bc7002c6` |
| zentriq-agent-linux-x64.rpm | `cb721aee4c9cb9d19f5e4ad2acadc63114f047826155345cf6dbbe31cbcd6390` |
| zentriq-agent-linux-x64.tar.gz | `7aeb49b19ff2ea42f835e78eae2c6b38b9da3d73e16984bb3e7cd7ad650771ec` |
| zentriq-agent-linux-arm64.deb | `9b5d8d47dbf68ad39d4d71202c76811955af5c57d4c1a0709c8e9ceae5fa0313` |
| zentriq-agent-linux-arm64.rpm | `e7aa79546b5e39e7b2dfc5841f0e07e1fecd6e4ab1385b4f0d84e8b8be1d0b29` |
| zentriq-agent-linux-arm64.tar.gz | `b2df355e8094b8156e438a313ab265dbb1849de5e3aa62565dac5190e0b0c84f` |
| Zentriq-1.1.1-msi-offline-kit.zip | `0d2f2f054a24c29b900c14965f0b35d4198764498cf3f293390bf2a42bad2ae3` |
| Zentriq-1.1.1-docs.zip | `6e1a9e39714765edc1f99487f4e76e755c1487d252f399bf39f54beec2232972` |

## Build 133

GitHub Actions run 43199242180, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.133` (pre-release,
**unsigned**), package version **1.1.1.133**, commit `7fa7a73`. Results: unit + PostgreSQL integration tests **482 PASS**
(484, 2 skipped); installer acceptance **91 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old
Windows); upgrade suites **14/14** (schema 42); Linux agent on 11 distributions **11/11**.

New in build 133 (go-live hardening): the console and agent port accept only TLS 1.2 and 1.3 (T100); console sessions
end 12 hours after signing in; signed agents accept only update packages signed by the same publisher; DigiCert
KeyLocker signing mode and configurable signature details in the build; dependency audit in the quick build. The first
audit found 12 known vulnerabilities (4 high) in the dependencies of the bundled MeshCentral 1.2.5; the web console and
.NET packages have none.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `fd9df6653c073fbb8fd42256142f1e8f53b10dfbf42f128e41ddd0343a134836` |
| Zentriq.msi | `14cfeaab2148306c18c0a1872592394a0ef97d668d56ba54489ad8312a22d044` |
| ZentriqAgent-x64.msi | `cf7cb3bdaf905711c4bd7aa348168928e44acba2cc2c88bd09ee03017330807c` |
| zentriq-agent-linux-x64.deb | `25251e575b8b9c543e582f163c52cb77ba2a6bbf9b047b6da40ce2fd7068345a` |
| zentriq-agent-linux-x64.rpm | `cd82064e9cd266f1509d4ad886e5375c91f58edb73335db77dbc720b1db17e33` |
| zentriq-agent-linux-x64.tar.gz | `0186a60b0a1729cf70633b3b91a415ba25280891e2bf2cb0932a27a4b789af4d` |
| zentriq-agent-linux-arm64.deb | `311e249351fba0a55bd0fa775dbd77857ddd01b133a9fb916b6d08f4558c035d` |
| zentriq-agent-linux-arm64.rpm | `0cff5bb77b29402f43d5e61a72d0b22a56a2d561511bb526c4c488be18681315` |
| zentriq-agent-linux-arm64.tar.gz | `669c654ed226bfa943b4b0699671902aa6014060f66c603562fddc59e067ebda` |
| Zentriq-1.1.1-msi-offline-kit.zip | `c490f0ec44f1726b97cfbed47618103580ab2d6ee6b76adc33dcee7666f67f01` |
| Zentriq-1.1.1-docs.zip | `0e41879485b114e37f312ce5f63e3d4a9d994bb7063ee829ff9bbfbc2dcb33b7` |

## Build 132

GitHub Actions run 43182444766, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.132` (pre-release,
**unsigned**), package version **1.1.1.132**, commit `9f6b98e`. Results: unit + PostgreSQL integration tests **479 PASS**
(481, 2 skipped); installer acceptance **90 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old
Windows); upgrade suites **14/14** (schema 42); Linux agent on 11 distributions **11/11**.

New in build 132: the Device Experience page has no tagline under its title.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `5178bebe8ac262438a9d34bc0df97969b46708dda931e709bb1bb9e8ebc6474c` |
| Zentriq.msi | `159041582298af17db0f63bdf91b52e61ceeed0e8a5f5f006891c6c4a29debf6` |
| ZentriqAgent-x64.msi | `52bcf9a9ed079425d7811923bdfb79af52c0c9e2f25729738c1553a20c11d73c` |
| zentriq-agent-linux-x64.deb | `46dbb0bb93acebfc5da8c2a1a0dcbc5541da4a69feee377578a016ee64c68b9a` |
| zentriq-agent-linux-x64.rpm | `50c14d7511645267b6f49617a1bf1a50af2f266ef6626579a1d1b6d3ed713b18` |
| zentriq-agent-linux-x64.tar.gz | `e42eb11db5dc8e713e053a490773c735f8a730cfb110e1c8922f7434dc71f145` |
| zentriq-agent-linux-arm64.deb | `9bcffd0eb0b6dc26e9661ddbf00367e2c1fe28f7dcdff53a90a92da83838bf38` |
| zentriq-agent-linux-arm64.rpm | `e6cc8c16de9522e16648a8fb81dfda38c5f92a1c3ef1d099007d6091bf10f1b9` |
| zentriq-agent-linux-arm64.tar.gz | `fc485565e30d117968c4cb6da708ad14942b4b5d6c189d6632741f4936e33c6a` |
| Zentriq-1.1.1-msi-offline-kit.zip | `9353a7d98e1225c56dd00fb7681129c11da75090bb2a3053317e6db00c3e2081` |
| Zentriq-1.1.1-docs.zip | `02137eb7e032e4bd9915eff9e5fff0f6d6624e10828f548e053ee85f80fea562` |

## Build 131

GitHub Actions run 43161426091, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.131` (pre-release,
**unsigned**), package version **1.1.1.131**, commit `c006e34`. Results: unit + PostgreSQL integration tests **479 PASS**
(481, 2 skipped); installer acceptance **90 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old
Windows); upgrade suites **14/14** (schema 42); Linux agent on 11 distributions **11/11**.

New in build 131: product keys found during inventory are kept encrypted, hidden by default and shown on request
(audited) — *Software Licenses › Product Keys* and View on the Activation tab for Windows and Office (T99); the product
and licence dialogs offer the software, publishers and versions found in the inventory as pick lists, with free typing
still possible; licence type **Retail**. See [Software license management](software-license-management.md).

Run 43148045322 (build 130) was not released: T92 failed (89 passed, 1 failed). An automatic patch scan was running
when the agent was installed again and enrolled under a new registration; the scan stayed *running* under the replaced
registration and blocked every new scan of the device. Fixed: when an agent checks in, open scans and queued work of an
earlier, no longer active registration of the same device are taken over (scans and queued work are queued again,
installs and scripts already sent are marked failed).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `0f6141fa127a22cfaa0faa2e3305a73f314078cbe399c0fd2d108a2b3d92e671` |
| Zentriq.msi | `ea09f40d79f0e200c958bb4a2c67fcd48c78ea97d2540e10492368bbab33196f` |
| ZentriqAgent-x64.msi | `d66695388ae30d540c513f2cc5b0f2cfe4aeafd7b58fc39b1117719267c0a9d4` |
| zentriq-agent-linux-x64.deb | `34e6005bcf43dfd0eafea34ce721d7ed681a9136a92681837c88071203ab47fb` |
| zentriq-agent-linux-x64.rpm | `24d44872541a1604ce5b0e0ae076c503b618f177d97dfd2529f568a244184c27` |
| zentriq-agent-linux-x64.tar.gz | `efa64eef20d6f3bf4a8c2fe8323c469114109e56fd8ca5a939d27d3ba6781d76` |
| zentriq-agent-linux-arm64.deb | `747602a9a782370aa64a7c606365b73ec3fce8db48bde5ce3c58a8a0950fd257` |
| zentriq-agent-linux-arm64.rpm | `9fcb20659a2c643ce371b5a254f7adbc6621e1e1cbd2619f11e9de375c811499` |
| zentriq-agent-linux-arm64.tar.gz | `dff94e6d8c58da56bf3a3fd436f193ad8c7cb80b43e7d7de9aeaa1f67a3431d7` |
| Zentriq-1.1.1-msi-offline-kit.zip | `cbc7c70c4dda3b02e1d8438f59a92e9af456942db3a8ae3c9f562f1dd4fe2034` |
| Zentriq-1.1.1-docs.zip | `815bf52af9ccac60ce9380adac7bd4aeaeddb8b082cfe70aa9689a3c66ded4e9` |

## Build 129

GitHub Actions run 43084157690 (attempt 2), workflow `windows-release`, on a GitHub-hosted **Windows Server 2022
Datacenter 10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.129` (pre-release,
**unsigned**), package version **1.1.1.129**, commit `3259a52`. Results: unit + PostgreSQL integration tests **464 PASS**
(466, 2 skipped); installer acceptance **89 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old
Windows); upgrade suites **14/14** (schema 41); Linux agent on 11 distributions **11/11**.

New in build 129: the Security Pack is part of the **Advanced** edition (no longer a separate add-on); the privilege
management, BitLocker and USB control pages have no tagline or intro boxes; the side menu has no dividing lines between
or inside modules, module icons on tiles, the open module as a gradient card and the active page as a highlighted pill.

Run 43066420069 (build 128) was not released: T92 failed — the patch scan kept running after Windows Update was disabled
because the service stayed *stop pending*, so the watchdog (which waited for *stopped and disabled*) never fired, and the
test's diagnostics read a field that does not exist. Fixed: the watchdog ends the scan as soon as the service is
disabled; the diagnostics print whatever fields the actions have. The first attempt of run 43084157690 passed every test
but GitHub's artifact service failed the upload (HTML error page), so publishing was skipped; the failed job was re-run
on the same commit, so the installers were rebuilt (Linux packages unchanged).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `9578f14b8b9e5c4080dee28852331da04ba5adfa66598207a3bfd2086abd8b70` |
| Zentriq.msi | `0d1fab359ae8f0a862b8d497e2ceb669f3126f0a9773f9e4fc8026edaf902297` |
| ZentriqAgent-x64.msi | `bc49d5ca4dba3262fd1de41aa8367a2976ba6d96dc475b0b7ba469b2b995e653` |
| zentriq-agent-linux-x64.deb | `dc3ac125606e845519960ab6d0532182bb03a842c8303bd8d55d47b21231c2ab` |
| zentriq-agent-linux-x64.rpm | `edc241bf332a88bc6acc5ca169b070022e0fefafad3072d02f33497ca984c38c` |
| zentriq-agent-linux-x64.tar.gz | `adf1f66eab86115e597a640252a80fef4e56570eaf54537d29d4f81c05dffcaa` |
| zentriq-agent-linux-arm64.deb | `ca091768dea4a8f0cfa94b1e3ae5e438aa27f39e5861d9f4e1f1233fe48e70a3` |
| zentriq-agent-linux-arm64.rpm | `1ee1a6d9eb175c4be817d4d29e730092b64e76ddbf8ca2e17b6619b5e8cc52a6` |
| zentriq-agent-linux-arm64.tar.gz | `78c10a946c4c7c6641056db7716873dfe4e2b38941f65aa16d703a759cb53562` |
| Zentriq-1.1.1-msi-offline-kit.zip | `960a6b55e8b64aa81c8fe875393228cde8f393ee1eec90c343f21eb438330f67` |
| Zentriq-1.1.1-docs.zip | `fa4508628d10a80e463a3392a3710c36dd4606c4b48320bde3e3cceb9c1c2eb4` |

## Build 127 (Release 4)

GitHub Actions run 43023450337, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.127` (pre-release, **unsigned**), package
version **1.1.1.127**, commit `175a853`. Results: unit + PostgreSQL integration tests **464 PASS** (466, 2 skipped);
installer acceptance **89 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old Windows); upgrade
suites **14/14** (schema 41); Linux agent on 11 distributions **11/11**.

New in build 127 (Release 4, Security Pack — separately licensed): endpoint privilege management (T96), BitLocker
management (T97), USB device control (T98). See [Security Pack](security-pack.md).

Runs 42954492328, 42968208381, 42982533057, 42995851817 and 43009387543 (builds 122–126) were not released; each stopped
at failed acceptance tests, fixed before the next run:

| Build | Failed | Fixed |
|---|---|---|
| 122 | T97, T98, T68, T92 | BitLocker added a recovery password to a decrypted system drive before the TPM check (and the reason did not reach the console); USB writes recorded at 0 bytes and before the agent had the policy; live connection deaf for 15 s after a wake; reversed outage; patch scan hung after Windows Update was disabled |
| 123 | T98, T92 | USB drive letters of GPT disks (Storage provider); interrupted patch scans handed out but not confirmed were never requeued |
| 124 | T98 | **The test model matched the runner's own virtual disks (including C:)**: test devices now by exact device ID, the disk Windows runs from is never restricted; block also takes a disk offline where turning the device off needs a restart |
| 125 | T98 | Strict-mode error in the new safety check of the test |
| 126 | T68 | Outage of zero length when a check-in arrived before the live connection reopened |

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `55927377ee8ccf4f981a00c4e6a84d757de81d3ba73dc1d344903f4bf01f67eb` |
| Zentriq.msi | `d0a063056b3ced3108e4dc37fe4c6d021667c94cf462d9562c52b13c5e839416` |
| ZentriqAgent-x64.msi | `c1e5466de6a49a3c382f383346de51e1a91614579f5fcf3c60299456c446c66f` |
| zentriq-agent-linux-x64.deb | `4cdef44bc41be5a62735925376da7e5832e08f1892e0e00db2afe6541af2218a` |
| zentriq-agent-linux-x64.rpm | `6fa9c8e448583c3c4e1c8f7ec7ea4bb74ee01092768a7bed38e8266b6e3248e2` |
| zentriq-agent-linux-x64.tar.gz | `ef5a5ff9128c709a0a61bd5da347edd2ebca4457f592c80b5d364c0741a31a16` |
| zentriq-agent-linux-arm64.deb | `92cc4829d512a16dd3dc7363edaf4be26c532fe5457df92f0d03d3373c775038` |
| zentriq-agent-linux-arm64.rpm | `9d8beae08f863923bc40b54fc33a89e7b4288722e45111483b918b73196784f4` |
| zentriq-agent-linux-arm64.tar.gz | `512369aded786c89a3bf61678dec8c138062914144a8e92107470d32dca2829e` |
| Zentriq-1.1.1-msi-offline-kit.zip | `3d9b335832d3ec200d09603e22cd8128dd52b2f838e01f1fccd0611eeaa02ca9` |
| Zentriq-1.1.1-docs.zip | `25d2e6c8d2724806d576abe0bcb863e05d95eaf0f1c261f41e93c7bde2b6fb0d` |

## Build 121 (Release 3)

GitHub Actions run 42932627468, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM plus an Ubuntu job for the Linux agent. Release: `v1.1.1-build.121` (pre-release, **unsigned**), package
version **1.1.1.121**, commit `0ce61b2`. Results: unit + PostgreSQL integration tests **459 PASS** (461, 2 skipped);
installer acceptance **86 passed, 0 failed** (10 not run: need hardware, a domain, a reboot or old Windows); upgrade
suites **14/14** (schema 40); Linux agent on 11 distributions **11/11**. (Run 42920619424 for build 120 stopped at the
last step of the new test T95 — a strict-mode error in the test script; fixed, no product change.)

New in build 121 (Release 3):

* **Software metering**: Windows and Linux agents record the programs run in users' sessions (minutes, launches, per
  user and day); Software Licenses › Software Usage, used / unused installations per product with the possible saving,
  Uninstall from unused devices, programs per device, alert for installations not used for 90 days; Settings: on/off,
  days without use.
* **Device Experience**: score 0–100 per device from start-up time, crashes and hangs, blue screens, unexpected
  shutdowns, service failures, processor / memory pressure, disk space, battery and uptime; fleet page with trend,
  problems, applications that crash most; device tab with events and resources.

| ID | Test | Result |
|---|---|---|
| T95 | Software metering and Device Experience | **PASS** — zq-meter-app.exe metered (3 min, runneradmin); product installation used; score 100 (good), 10 samples; metering turned off and on again on the device |

Tests added: `Release120Tests` (the agent's recorder, system programs not metered, the score, catalogue programs) and
`Release120DbTests` (usage and experience stored with the inventory, sent twice without duplicates, used / unused /
recent installations).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `2c2226250e8ba94da93a680481bf7a09322ca1f769b3c92d426f75ae1f8db8c1` |
| Zentriq.msi | `660dd0eeecabfb798acedba887f00d50da5607511a71b48ab8c78dd439a87a0c` |
| ZentriqAgent-x64.msi | `52391f9d2f1de1276a17d3adb3dc2eac8613c761ab6cab65c30d7f5ccf00fac9` |
| zentriq-agent-linux-x64.deb | `85845001ee75b2f4d9a8541d77f21f4470ca096f3b4859abaa8365bb4e698b5d` |
| zentriq-agent-linux-x64.rpm | `334c6c74d82dd342af2026b9030d3b3effda2ac2e59ae4bd4cf4031d927fb866` |
| zentriq-agent-linux-x64.tar.gz | `30d8bbfa402d115967d47abe2d78a6ecd3c0f82d6012fdb24296909524cd2bbf` |
| zentriq-agent-linux-arm64.deb | `01c41538c4ab9471eb35cd645e5d94720f0d2bc6b25da4eea75360b3f5b9e2dc` |
| zentriq-agent-linux-arm64.rpm | `4d52795462b6dcfa6fc4f06bf1e81a6a561998b74037f08a05ebdb23c3860fd9` |
| zentriq-agent-linux-arm64.tar.gz | `0ac45733402e6f4de7d26f7c24ce682954c35fbad68a2a97db1a0cab32022e79` |
| Zentriq-1.1.1-msi-offline-kit.zip | `97d3e00d0ab69da58326ef64f75244845f9a19bea5376415b338d5817ea27649` |
| Zentriq-1.1.1-docs.zip | `904f5844be20f51c3188c4794c835eeeafb3e9aea67fd844226228896cb9c50d` |

## Build 119 (run 42868622076, commit `a5ab68e`)

Results: 448 PASS (2 skipped); acceptance 85/0; upgrade 14/14; Linux 11/11.

New in build 119:


* Console menu: each module is a heading with an icon that opens and closes its pages (one module open at a time; the
  module of the current page opens by itself); pages stay in boxes, indented under the heading.
* Network Inventory: switches, routers and neighbours open inside Network Inventory (`/network/devices/{id}`).
* Remote sessions are compact: Desktop, Files (and Terminal when allowed) only; registry, software, events, details,
  console, Intel AMT and plugins are hidden and their rights not granted.
* Licences: addresses that do not answer are never added; addresses answered only by refused connections (routers,
  firewalls) with no name, MAC or device type are listed in the scan result but not added; devices without an agent
  that did not answer their last scan stop counting until they answer again; agents always count.
* A scanned address that an agent reports as another adapter of its device is merged into that device (one licence).
* Filters stay until cleared (per browser); page controls at the top of every paged list as well.
* Printers: recognised by SNMP device identifier (about 20 makers) without the Printer-MIB, model from the printer
  name, more printer web pages, ports 9101/9102, longer SNMP wait.
* Tray: Get Remote Help has its left margin (build 118 had none). The screenshot shows the problem box and the
  request list still running past the right edge; fixed on `dev/ui-119` for the next build.

PostgreSQL test added: `Release119DbTests` (unreachable device without agent takes no licence and counts again when it
answers; agent device always counts; Wi-Fi address of an inventoried laptop merged into it). Unit tests: printer makers
recognised by device identifier, compact remote session flags (`hide=53`) and rights.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `1743088abf1720a93f8fcab52deee338b576ca37439b0cf1b4a7b7adbdb0c3e6` |
| Zentriq.msi | `2c8b263110d740692d1f0b82e6fae48a5199c1b4ee6da1c14f11f351f56c82e6` |
| ZentriqAgent-x64.msi | `8876cc333bf9ff177f44d54fe752963f008e80283d614185611e54624e04680a` |
| zentriq-agent-linux-x64.deb | `f3f92f9460c2add7175aa508671bb29f9b640a6cf7c59dd0344e1ba96d48161a` |
| zentriq-agent-linux-x64.rpm | `0cc62996500c805fe2064a9f12eace8405d420242b1d990511c4a82b31de7fcd` |
| zentriq-agent-linux-x64.tar.gz | `be0765f18b3a02b33b41a7e619eeae52ae2ced64d2812c05d3c68333c8cdcd16` |
| zentriq-agent-linux-arm64.deb | `7161d43f851ed0308d0fd94758318ec7d0a2f884c7feb30d03a7b02e8c174638` |
| zentriq-agent-linux-arm64.rpm | `a1cd9921dd85852f180ddd2c018ae42de26f2a2dc6bbab132167e7e2f90c98d9` |
| zentriq-agent-linux-arm64.tar.gz | `0466ad38039e69d178a8439c0d6cedd0de15f2b8cfb4d47a84d75bdbe9210373` |
| Zentriq-1.1.1-msi-offline-kit.zip | `470a19bbadff62d5670f39eab497960db02a67c97f37a65f14bf2368d4cca5a6` |
| Zentriq-1.1.1-docs.zip | `a5937539dcdb8c9e7a086da9511bbbc926ed0bbe9db8863ce2b0feb3d3eebb4e` |

## Build 118 (run 42365189724, commit `45e353f`)

Results: 439 PASS (2 skipped); acceptance 85/0; upgrade 14/14; Linux 11/11.

* Tray icon of the Windows agent for every signed-in user: **Software Center** (software IT offers, installed by the
  agent as SYSTEM without administrator rights, optionally after approval in the console), **Get Remote Help** (help
  requests in Remote Support › Help Requests with Connect, Take and Close), **Device Information** and **Check In
  Now**. Console: Software Deployment › Self-Service (offered packages, requests to approve, tray settings).
* Console: menu items in boxes; every add / edit window rebuilt in titled sections with aligned fields (devices,
  accessories, IT assets, Non-IT assets, patch policies and installs, scripts, runs and schedules, licences and
  products, web apps, deployment rules, packages, groups and deployments, security baselines and exceptions).
  Screenshots of the windows are taken in every quick CI run (`ci-quick-logs/screens`).

| ID | Test | Result |
|---|---|---|
| T94 | Tray icon: started on the user's desktop; Software Center installs offered software without administrator rights (and after IT approval); Get Remote Help reaches Remote Support; device information | **PASS** — tray running for the signed-in user; installed without approval and after approval; help request #1 in Remote Support and closed; agent 1.1.1.118 |

PostgreSQL tests added: `TrayDbTests` (offered packages per platform and device group, install at once without a second
job, request and approval, missing package file, help request numbers, tray settings).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `d190df666936884cedb03524f2cab6886b8fce9a9e1cd7bac0d5a60a22163ed0` |
| Zentriq.msi | `0ec845703c0acd747c8f817606f5e30492f65eaa3d968808b3af26dbb1182e7a` |
| ZentriqAgent-x64.msi | `2450ffabffd6396c3900871d25b1040d7cf55a79dac552f5fc77678812c1729a` |
| zentriq-agent-linux-x64.deb | `b95aaa8185d5592513d7892d4b7a2f57860537a6b473ecfbe8f86d6ea86980bc` |
| zentriq-agent-linux-x64.rpm | `6003aeb5eaf2e9f552f9a333935ed73db52b43138d78a9339ccae6d1ecafe85d` |
| zentriq-agent-linux-x64.tar.gz | `ba6abcfb88579f6cc411becde09f07146a248297c7e85e4937150254e60782a1` |
| zentriq-agent-linux-arm64.deb | `6eba4e8034a97011c0356ea4b41a0d0a5127fd8bd45c67fc0b98e6a216ea994c` |
| zentriq-agent-linux-arm64.rpm | `9d6ca6999f382249526dfd70135120b0ca3b9cf102ca4b029ee7ef8c3e8972ea` |
| zentriq-agent-linux-arm64.tar.gz | `d4a10124f28b0460e0b6cb5c2d5fee4ba1fccd0ad128a0c920e75431cb891910` |
| Zentriq-1.1.1-msi-offline-kit.zip | `f9aa903ee08876c39162f3543523bc93110f824a409e7f71adc6eb33ff11ad28` |
| Zentriq-1.1.1-docs.zip | `942e0577cbd46bf568c58aaff0dc3af2b7ed83453ebd2c70881356f6e340191e` |

## Build 117 (run 42353554714, commit `678aa0d`)

New in build 117:

* Console: menu sections in title case (Discovery & Inventory, Remote Support, Secure & Remediate, Insight & Admin),
  brighter menu, section headings and "Asset Intelligence"; darker secondary text in the light theme; fields side by
  side line up (boxes at the bottom of their row, check boxes centred on the boxes).
* Application control: the block message comes back only when a person starts the installer again; retries by the
  software, an updater, a script, a task or a service are stopped without a new message for 4 hours (also for
  re-unpacked copies, by product name and version).

| ID | Test | Result |
|---|---|---|
| T83 | Application control on Windows, now also: after the message was closed, a retry started by a script host is stopped without a new message | **PASS** |

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `77c912979dd1f8eda5d8f95e8da2c226c9c7e1de0df43ffb62d979f3ccb929fb` |
| Zentriq.msi | `a7201e6b7e28dd2370194cabc49f2169dd1fe789a6e5c3966c625740e5698543` |
| ZentriqAgent-x64.msi | `23fdabded5d43c5a5b7375840ebe70bf8d39a62def544e2a22df75f69b11ee2e` |
| zentriq-agent-linux-x64.deb | `b8d554140d7fded5bda785a1d47846acb0715efb830fe54c94dbc60de2e4dd29` |
| zentriq-agent-linux-x64.rpm | `cf954f0167d8c5be5d7da36f230bd115152045de8edcf7c495843a4d988bba59` |
| zentriq-agent-linux-x64.tar.gz | `3cba50cfc49c9aebef1c33e6b0385f6e910863687025f1d112b0dbdc9d249957` |
| zentriq-agent-linux-arm64.deb | `4f1d8dce4cf47686b2c815ed9fd7229aa6c5d23ae39a63dd43ab3b1e7058475d` |
| zentriq-agent-linux-arm64.rpm | `46eb1d4791037125a60764e9c7e58de4032c3c0db33427c0037cf850f91ecada` |
| zentriq-agent-linux-arm64.tar.gz | `0b7b80b8a6ba81cfe5a0c94645706bc0f8928dad08cd11a9f5ab0d56a3d7719a` |
| Zentriq-1.1.1-msi-offline-kit.zip | `0dd84f81f5db84b5e2fb3a1a407e82bf93ce1195b7519316e3414f7ccb3860a4` |
| Zentriq-1.1.1-docs.zip | `ac820b66e11f1c069d7ffe9137942d1cb7c0fbc865486a50df5857a02f88e6c4` |


## Build 116 (run 42343815521, commit `da6a40f`)

New in build 116 (Release 2 of the Endpoint Central comparison; builds 114 and 115 failed T92 / T93 and were not
released — fixed: a scan was lost when the agent restarted, the Disk Space Report script printed nothing):

* **Patch Management** — the agent scans for missing updates (Windows Update Agent: Windows Update or WSUS; apt, dnf,
  yum, zypper on Linux), every 24 h and on demand; missing updates with classification, severity, CVEs and devices;
  approve / decline; deployments to devices, groups, departments or all with restart behaviour; automatic deployment
  policies with a maintenance window; device patch state (missing, failed, restart pending, installed).
* **Scripts & Remediation** — script library (PowerShell, Command Prompt, Bash, sh, Python) with 12 ready-made
  scripts; runs as SYSTEM / root with parameters, time limit, output and exit code per device; checksum checked by
  the agent; remediations (detection, fix, check) and schedules; can be turned off; everything audited.
* Long agent actions (patch scans and installations, scripts) run beside the check-in; one interrupted by a restart
  is scanned again or reported as interrupted.

| ID | Test | Result |
|---|---|---|
| T92 | Patch management: real Windows Update scan of the runner (0 missing), device state, policy validation, a deployment of an update the device does not miss is skipped with the reason, settings validation, activity log without the payload | **PASS** |
| T93 | Scripts: PowerShell as NT AUTHORITY\SYSTEM with parameters, exit code 3 and stderr kept; ready-made Disk Space Report; remediation (remediated, then compliant); 10-second time limit; turned off; ready-made scripts cannot be deleted | **PASS** |

Not testable on the build VM: installing real Windows updates (the runner was up to date), a Linux patch scan on the
11 distributions (the Linux agent's parsers are unit-tested with real apt, dnf, dnf 5 and zypper output).

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `a12078e6d80e181107861a54e3e25af83b42800167864b2afc42668b8bebf899` |
| Zentriq.msi | `081987ed90921000633b49cd2154fe433411d585ab91d1eea33289e681531b48` |
| ZentriqAgent-x64.msi | `d06f81e9f7450c493e560bc98a36428510e79f557a9b8eb3c89d2a0c04eecf36` |
| zentriq-agent-linux-x64.deb | `07fade1270c94739a090112e74de99a221f01a106134f74c224c4654cb28549a` |
| zentriq-agent-linux-x64.rpm | `141e9e46a60a17c6240a375ec26fd18364eb604ab40412fa839dae92de748d36` |
| zentriq-agent-linux-x64.tar.gz | `1f285e318cf081853a77200be6ee621e6a037e48d56555b7ed1a16480ec9dbf6` |
| zentriq-agent-linux-arm64.deb | `93d77c94f5e5e9d5db78c31a063f9a26cd77163328b62075d4a12a2e0e158092` |
| zentriq-agent-linux-arm64.rpm | `65b1e21d9bd3bcbb36791757ad44f30a851a9f3adff855beea8fd4afd5abcadd` |
| zentriq-agent-linux-arm64.tar.gz | `3061b0a562c3d391dcac620007e54903241f93ca6628df7cd652ff8ffc0f5508` |
| Zentriq-1.1.1-msi-offline-kit.zip | `caf99975a560fcf45e270677eae761d1c025739ae344668d0810b13e5c4557e5` |
| Zentriq-1.1.1-docs.zip | `8d3520acc310e20d7afedf55c4e6cd6899d6408f00ea490b7ba92e3365920d2a` |


## Build 113 (run 42253237747, commit `23fed34`)

New in build 113 (Release 1 of the Endpoint Central comparison):

* **Online warranty lookup** — Dell, Lenovo and HP API keys under *Asset Lifecycle › Settings* (stored encrypted),
  *Look Up Warranty* in *Warranty & Contracts*, for selected IT assets and on the device's Lifecycle tab; automatic
  lookup every 30 minutes for new and due devices; manual and imported dates are kept unless overwriting is turned on;
  dashboard widget *Warranty Expiry* and highlights. The agent now reports the system SKU (HP product number).
* **Remote session recording** — *Remote Support › Settings*: on/off, all devices or device groups, retention (90 days
  by default); the consent prompt and privacy bar show "(session recorded)"; *History* has *Play* (desktop and
  terminal player) and *Download*; every playback is audited (permission *remote.recordings*).
* **Console sign-in with Active Directory (LDAP/LDAPS) and Microsoft Entra ID (OpenID Connect)** — *Users, Roles &
  Groups › Sign-in*; directory groups mapped to roles; *Directory Account* and *Sign In With Microsoft* on the sign-in
  page; local accounts and two-step verification unchanged.

| ID | Test | Result |
|---|---|---|
| T89 | Warranty lookup: keys stored encrypted, a made-up Dell key is explained (HTTP 401), devices of other makers are skipped, expiry overview | **PASS** |
| T90 | Session recording: settings validated, the engine is configured to record, recordings list and playback protected, turned off again | **PASS** |
| T91 | Directory sign-in: *Sign In With Microsoft* redirects with PKCE, a forged callback is refused, an unreachable domain controller is explained, group mapping added and removed, local sign-in keeps working | **PASS** |

Also: the live remote engine test now starts MeshCentral with recording on and checks the recorded technician account.
Not testable on the build VM: a real vendor warranty answer (no real keys), a recorded desktop session (no device with
a screen), sign-in against a real domain controller or Entra tenant.

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `35932adf439513d1da8428840f99d45191f1b7a1a90be8d0b3c1ca629159cc79` |
| Zentriq.msi | `2fee7403108e5a47deca87362f7db3df1ed31b17349251c498eedb5a9a1ddda4` |
| ZentriqAgent-x64.msi | `db00a58fc4a8a440811db9a2eea7449990fc3630b244d2bff2392c5e33607570` |
| zentriq-agent-linux-x64.deb | `c66e29cbb5d39b47ff49e5087c8d957ee3c360f15cd9e94c84d7aeb54edb8c6e` |
| zentriq-agent-linux-x64.rpm | `5767a14036eed5f42a6a8ebef4f34b1f438cc5cd207d4686f9ced849e30ad38a` |
| zentriq-agent-linux-x64.tar.gz | `2685ab9d6ceb6117d01c9ab553b43ce0d41ad8ffaa77bdb211ee7c5514afc2cd` |
| zentriq-agent-linux-arm64.deb | `c9bf9a13d5178ebbea7835dc83d2c80ec0b98555fa2a04ad08570d8a02ff861a` |
| zentriq-agent-linux-arm64.rpm | `ea5032333c49014f0b5c6bb692b2e67c79ebf464cadfb82e3775fa5668d01b53` |
| zentriq-agent-linux-arm64.tar.gz | `a6968ee37268d9a9265d09b1b3f259318c79081959e1dfc60e10b6c499558329` |
| Zentriq-1.1.1-msi-offline-kit.zip | `0cecf31a83cc180de98d74a5bbc6d25f5dcf929a91f0e95a303fb5381dbd0d53` |
| Zentriq-1.1.1-docs.zip | `f2889c164de75cd418d501e93c82320224fd20d2e4480d6c095bd174281ae4c9` |


## Build 112 (run 42094367710, commit `4d6c8cb`)

New in build 112 (build 111 failed two new acceptance tests and was not released):

* **Software License Management** — products and how their installations are found, licences (perpetual, subscription,
  volume, OEM, enterprise, trial, freeware), allocations, the licence position per metric, Microsoft 365 cloud licences,
  Windows and Office activation reported by the agent, expiry tracking, alerts (bell, e-mail digest, integration events)
  and dashboard highlights.
* **Web Apps & Accounts** — catalogue of 87 SaaS apps, use seen on devices (DNS cache; only known apps are kept),
  Microsoft Entra single sign-on apps and their accounts, CSV import of user lists, orphaned and inactive accounts,
  shadow IT.
* **Network inventory** — connected devices register (every MAC address on an access port with IP address, IEEE
  vendor, type, switch port, VLAN and moves), subnets and IP address use, IP conflicts, unknown device events.
* Every word on buttons, tabs and menus starts with a capital letter; Title Case for tiles, panels, dialogs and column headers.

| ID | Test | Result |
|---|---|---|
| T86 | Software licenses: Windows activation reported by the agent (ServerDatacenter, Volume:GVLK, licensed); a product found in the inventory with a subscription of 0 licences ending in 5 days raises a shortfall and a high expiry alert; renewal resolves both | **PASS** |
| T87 | Web apps: catalogue (87 apps), CSV import of 2 accounts, the leaver's admin account orphaned with a high alert | **PASS** |
| T88 | Network: connected devices, subnets, MAC vendor status and notifications respond | **PASS** |

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `99ffc629f96be4544cf6fc74c773fd168599d820cac63bda8a9dac31b6cafcc0` |
| Zentriq.msi | `6c6ef461ea212f8d964547d1aa2a97d3e5afd1ee45be051fbbcf1d0cc3336d71` |
| ZentriqAgent-x64.msi | `75cfab8754f4b3a6a7430aafd51acadceffdc274dcb71b608e3dba802edaf266` |
| zentriq-agent-linux-x64.deb | `d9508077092cb25e9c06144a7ed378f2af68deddbd0b481902bc5ca2ee80f691` |
| zentriq-agent-linux-x64.rpm | `890b6e6f3d039c911290c239c03b2d0b760115df26d1a3dcae634da997d45e7f` |
| zentriq-agent-linux-x64.tar.gz | `ffc2a67b67788e7c540af60d315ea0c55a9db1bf558a672c78caf0ea5c70c176` |
| zentriq-agent-linux-arm64.deb | `68c35b7b5f5acbbcd30852595ebce027d844ccba3bf57eec5f8a473c57f27cb6` |
| zentriq-agent-linux-arm64.rpm | `7cb3db5e19077d384da9fd170e275ddb02596581cc85680a94ef59b2f5e10a2b` |
| zentriq-agent-linux-arm64.tar.gz | `6e286a71d2a9b3e27fae47f390ae99719dc50a779dca7ff96a9b3ba1ad54bbe2` |
| Zentriq-1.1.1-msi-offline-kit.zip | `df779f3bbdd780e2446c1b8124dc2c3f5b16845c8b44a00454723955bcf9df6a` |
| Zentriq-1.1.1-docs.zip | `b2d7c72dbedd5af81ecf5d8c5622d2e231c8521f315be1c999df01f52f78580a` |

## Build 110 (run 41966535331, commit `b1c1df3`)

New in build 110: console polish and Software Deployment refinements.

* Software Deployment: no tagline; Title Case tabs and tiles (also on the other modules); every tile opens its tab
  (Blocked / Found Installed open *Events* filtered); shorter, compact text across the console; darker text colours
  for readability.
* *Found on Devices*: click a row to see which devices have the software and approve it for one device, a device
  group or all devices.
* The blocking message on Windows appears once per attempt: closed means closed; retries by downloaders,
  bootstrappers or scheduled tasks are stopped silently; running the installer again shows it again.
* Requests and events carry the full installer details (product, version, publisher, installer type, file, size,
  SHA-256, MSI codes, signature, download origin, parent process and command line) and the device (OS, IP,
  department, agent version, last seen); Linux requests carry the repository's package information.

| ID | Test | Result |
|---|---|---|
| T83 | Windows application control: audit reports; block stops the installer within 2 s with the message, **once per attempt** (second start while open: no second message; closed: does not return; run again: shown again); request with **20 installer details** and the device's OS; approved for this device by SHA-256: runs | **PASS** |
| T82, T84, T85 | Catalogue and inventory approval, push deployment, remote support agent protection | **PASS** |

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `7f677b4195d6230e9ed071b29b07f3125d8545f07c9620e1979d109a84c8c92f` |
| Zentriq.msi | `5097fbc5ed121521e70b0ce9a48043d80a43a9fddb107e398592f262cde2d1a6` |
| ZentriqAgent-x64.msi | `e9d8b9da03ff66e27b482af7abdd7342da36c1fb33aa4962842ca033b5f2834d` |
| zentriq-agent-linux-x64.deb | `428455c83f4fbc7bd24bf7f2a5a88a1524e6f34646fa95e6563b8211e9099c67` |
| zentriq-agent-linux-x64.rpm | `2c510a7e20ad2218c69cfd1468e37d9ebbb6c137b25a567e7718badc64889cab` |
| zentriq-agent-linux-x64.tar.gz | `52084b4ffce1f2a2aaca86509d4f93e0eccb544d681cfff3b71571d9a5a36282` |
| zentriq-agent-linux-arm64.deb | `64439dac491b9ed9f2573804fb5cd4cffa373479e8f1079a10424d4defed09cd` |
| zentriq-agent-linux-arm64.rpm | `183565aae75dcfde0a15e6588761c23b64ee84544aa23dce096bf29380d6ac5c` |
| zentriq-agent-linux-arm64.tar.gz | `90d85a462838fecb1a57a8fc7215916f385cd234aadd058642b6acf77b73b8d3` |
| Zentriq-1.1.1-msi-offline-kit.zip | `cdd9bcdd92b109cb28433c250ed956f6aee21a0f07523866aabb1ba23e04a18c` |
| Zentriq-1.1.1-docs.zip | `5b11ae878ca021d94b7b58c53b2c1c7b369b8ef28ed32d4ca12bcc21c49b0837` |

## Build 109 (run 41934323520, commit `968c267`)

New in build 109: **Software Deployment** (Secure module) and **remote support agent tamper protection** (builds 104-108
failed only in the CI test harness or on test timing and were not released).

* Standard software catalogue (201 well-known applications for Windows and Linux) from which administrators build the
  organisation's approved software list; own catalogue entries; approve from the catalogue, from the installed
  inventory or from a user's request; block rules for all devices, a device group or one device, with expiry.
* Application control per organisation or device group: off, audit (report) or block. Windows: installers are stopped
  before they run, the user sees the message and can request approval; optional WDAC (opt-in, audit first). Linux:
  dpkg/apt and dnf refused through package-manager hooks, zypper reported. Unapproved software found in inventory is
  reported and can be removed automatically.
* Push deployment of MSI/EXE/DEB/RPM packages with per-device status, retry and uninstall.
* Policy changes reach online agents within seconds and the agent reports them at once; the agent's hooks and WDAC
  policy go only with the agent, which needs the uninstall code.
* Remote support agent (ZentriqRemoteSupport) protected like the Zentriq Agent: stop, disable, delete and reconfigure
  refused for local administrators; reinstalled automatically if removed; released from the console.

| ID | Test | Result |
|---|---|---|
| T82 | Catalogue (201 entries), approve from catalogue and inventory, block rule, test, settings; audit mode reaches the agent within seconds | **PASS** |
| T83 | Windows application control: audit reports, block stops the installer within 1 s with the message, approval request from the message, approved installer runs | **PASS** |
| T84 | Push deployment: upload, install with per-device status, failing package reported and retried, uninstall; WDAC audit for a device group | **PASS** |
| T85 | Remote support agent tamper protection: stop refused (exit 5), disable and delete refused, console shows protected, lifted when protection is off | **PASS** |
| A01-A05 | Linux application control: Ubuntu 24.04 (apt), Debian 12 (dpkg -i), Rocky 9 and Amazon Linux 2023 (dnf) block; openSUSE 15.6 (zypper) reports | **PASS** |

| File | SHA256 |
|---|---|
| Zentriq-Setup-x64.exe | `5453bd0d8af76206473563c1fd5217131470586da3b4bf9bea513bd5a171df18` |
| Zentriq.msi | `3abba4f425bc03a3707c848027d8b97ac081355c641471bcf82d93e340aeb452` |
| ZentriqAgent-x64.msi | `353180652fb1e80bbe0ffd8ecc5c322a1b012ef99ae30bfb76e4df0fec4e6bc0` |
| zentriq-agent-linux-x64.deb | `5c9273be44bfa2e7e2b16761f790551a1fa6aecf41bd6cad73be83930c7cdeb4` |
| zentriq-agent-linux-x64.rpm | `0ad3841a0e1e9e2bf0bfcbe8c53efc8f028d220ec354741e2b0682ad16749d35` |
| zentriq-agent-linux-x64.tar.gz | `759882744c91f174631e6430c1fa7661cca3a4b17d9d52a37685e6ebe4d9cd9f` |
| zentriq-agent-linux-arm64.deb | `73e713d1064073c104554769b0b6e496d20da702049580790ac85ccdea8f899b` |
| zentriq-agent-linux-arm64.rpm | `1c4855906dfba0fc95277dfe164170ed6e228846d26fddc61580349793ed3057` |
| zentriq-agent-linux-arm64.tar.gz | `8527c06775a7a80c0a796c63038ede201c2cd662c9b678a0ed29958932bd3eb0` |
| Zentriq-1.1.1-msi-offline-kit.zip | `909b8a048538a694af4781cbee0fbdc1b8d900653e5b450bd46c5cbe2c1306b5` |
| Zentriq-1.1.1-docs.zip | `403f4e2d0a3095a3173958e42a275f9d8668608343222a6c3de6b291b416efe6` |

Previous: build 103 (run 41283941378): 369 PASS, 2 skipped; acceptance 72 passed; Linux 11/11.

New in build 103: Zentriq trusts the **production license signing key** `zentriq-prod-2026-11` (fingerprint
`SHA256:56:39:37:29:89:E5:08:18:1F:DC:2A:CB:94:7D:64:1A:8B:C4:93:A6:E6:A6:3D:44:21:11:67:BB:32:F8:94:81`, confirmed by
the key owner); the development key `zentriq-dev-2026-10` is revoked. A unit test pins the fingerprint.

New in build 102: **Zentriq Agent for Linux** (builds 100-101 failed only on test assertions and were not released).

* Packages for x64 and ARM64: `.deb`, `.rpm` and `.tar.gz` (self-contained, systemd service `zentriq-agent`), served by
  the server and attached to the release. One-line install command in *Agents › New enrollment code* (downloads from
  the agent port trusting only the pinned server key, checks the SHA-256, installs and enrolls).
* Identifies the distribution and its family, version / LTS, **edition** (e.g. Ubuntu Server, RHEL Server, Fedora
  Workstation, SLES / SLED) and **server or desktop** (stated edition, meta-packages, display manager / graphical target
  / sessions, chassis), desktop environment, kernel and package manager; plus the full inventory (hardware, packages,
  services, users, security settings).
* Same features as Windows: live connection, schedules, service start / stop / restart, package uninstall, self-update
  per platform and format, remote support agent for Linux, tamper protection (package removal and `systemctl stop`
  refused without a code, watchdog timer), Linux checks SL-01 to SL-13 in Security Configuration.

| ID | Test | Result |
|---|---|---|
| T81 | Ubuntu 24.04 in WSL against the installed server: console install command, enrollment, inventory (Ubuntu Server, Server), live connection, 11 Linux checks evaluated, package uninstalled through the console, removal refused without a code and done with one | **PASS** |
| D01-D11 | Ubuntu 24.04 / 22.04 (desktop), Debian 12 (deb, tar.gz), Rocky 9, AlmaLinux 9, Oracle Linux 9, RHEL UBI 9, Fedora 41, openSUSE Leap 15.6, Amazon Linux 2023: install, identify, collect, remove | **PASS** |
| T53 | Security configuration with 43 checks (30 Windows + 13 Linux) | **PASS** |

**License server** (separate product, workflow `license-server`): release `license-server-v1.1.0-build.13`,
`Zentriq-LicenseServer-Setup-1.1.13.msi` (SHA256 `ebabe8222cc0570a87373f44274bd7c06320e54932079fbd4483b98179215f68`).
One Setup for Windows Server instead of the zip and command prompt: it installs the server and its own PostgreSQL as two
Windows services and opens a browser setup wizard (owner, signing key created or imported, key backup and product
entry, who can reach the server: this computer only, or HTTPS with Let's Encrypt or your certificate). The key unlocks
by itself after a restart; daily database backups. Tests: 14/14 (Linux, incl. the wizard), Windows Setup test 7/7.

| ID | Test (license server Setup, Windows Server 2022) | Result |
|---|---|---|
| S01 | Silent install: services (server as its own virtual account), loopback-only listeners, portal leads to the wizard, data folder closed | **PASS** |
| S02 | Wizard: owner, key ceremony, product entry, encrypted backup, finish only after the backup is confirmed | **PASS** |
| S03 | Subscription offline file, perpetual online + check-in, customer license file bound online, tampering rejected | **PASS** |
| S04 | Both services stopped and the server started: database starts as dependency, key unlocked without a prompt | **PASS** |
| S05 | Public mode with own certificate: service restarts itself, HTTPS with that certificate, wizard 404 from the network, port taken by another program reported (server keeps running), back to local | **PASS** |
| S06 | Daily and on-demand database backups | **PASS** |
| S07 | Uninstall keeps the data; reinstall continues with the same key and licenses | **PASS** |

New in build 99 (builds 96-98 did not pass acceptance and were not released):

* **IT accessories: add categories.** The category field offers *+ Add category…*; the *Categories* button adds,
  renames and deletes categories (in use: refused). Categories are kept in their own table (migration V0031, 15
  built-in categories plus the ones already used).
* **Agents: no "Tampering blocked" message** on each device; only *Protection paused* is shown.
* **Agent service Stop / Restart with a code.** Stop and Restart in the Services console (and `net stop`, `sc stop`,
  `Stop-Service`) are no longer greyed out. While tamper protection is on, the request is held (*Stopping*) and a code
  dialog opens for each signed-in administrator: with the device's code or the code for all agents, **Restart service**
  restarts it and **Stop service** stops it. Windows does not let a service refuse a stop it has received (an SCM test
  in build 97 showed that a refused stop leaves the service unable to take any request, error 1061), so without a code
  (cancel, wrong code, two minutes) the agent completes the stop and starts again by itself at once, and the attempt is
  reported as tampering.

| ID | Test | Result |
|---|---|---|
| T78 | Stop without a code: held (StopPending), wrong code refused, cancelled → started again at once (new process); code + Restart → restarted (new process); code + Stop → stopped; disabling refused; console pause / resume; device code and code for all | **PASS** |
| T67 | Uninstall protection; stop without a code held and started again | **PASS** |
| T69 | IT accessories incl. built-in and added categories | **PASS** |
| T29, T47, T60, T79 | Revocation, updates and remote support with the new service host | **PASS** |

New in build 95: **license files validated by the license server**. Zentriq sends the customer a signed license file
bound to no server; *Licensing › Import license file* checks the signature, has the Zentriq license server validate it
(license active, newest file, activation free) and only then activates the license, bound to that server. Air-gapped
servers use the same file on the license server's offline page. The license server, its signing code and tool and its
documentation moved out of the customer product (`licensing/`); customer packages carry only customer documentation
(no test report, no code-signing notes).

| ID | Test (license server, Windows local edition) | Result |
|---|---|---|
| L08 | License file for the customer: validated and bound by the server; a reissued file replaces the old one | **PASS** |
| L01-L07 | Setup and key ceremony, start, sign-in, subscription and perpetual licenses, online/offline activation, tamper detection, backup | **PASS** |

New in build 94: **key ceremony** for the production license signing key (generated on the license server's own
machine; ECDSA P-256; encrypted PKCS#8 with AES-256 and PBKDF2-SHA256 600,000 iterations; owner-only file; SHA-256
fingerprint; ceremony record; sign/verify self-test at every start); **Verify file** in the license portal and test
files for license generation; **local edition for Windows** (`Zentriq-LicenseServer-Local-win-x64-1.1.1.zip`:
self-contained server, portable PostgreSQL 17, `zls setup | start | stop | status | backup | key-info`); Zentriq
accepts `http://localhost` as license server address for local testing.

| ID | Test (local license server, Windows PowerShell 5.1) | Result |
|---|---|---|
| L01 | Setup: own PostgreSQL, key ceremony (encrypted key, owner-only ACL, public entry, record), owner account | **PASS** |
| L02 | Start in the background; portal and offline activation page answer | **PASS** |
| L03 | Owner signs in; portal shows the ceremony's key id and fingerprint | **PASS** |
| L04 | Subscription license (3 years): offline file verified with its entitlements | **PASS** |
| L05 | Perpetual license: online activation; support extension arrives with the next check-in | **PASS** |
| L06 | A changed license file is rejected (bad signature) | **PASS** |
| L07 | Database backup and stop | **PASS** |

New in build 93: licensing by **IT devices** (per device, network devices included), **non-IT packs** (250 / 500 / 1,000 /
2,000 / 4,000 / unlimited), **cloud and VM instances** and **remote support technicians**; IT accessories unlimited.
Trial 30 / 50 / 10 / 1; **free tier** 25 / 25 / 5 / none (Standard edition). Subscriptions for 1, 3 or 5 years, perpetual
licenses with yearly (multi-year) support. **Online activation** with a license key, daily check-ins with a 30-day lease,
deactivation; offline activation kept for air-gapped servers. New **Zentriq License Server** (`licensing/`, released as
`Zentriq-LicenseServer-1.1.1.zip`): activation API, offline activation page, license portal.

| ID | Test | Result | Detail |
|---|---|---|---|
| T81 | Licensing plans and online activation | **PASS** | plans and metrics reported; bad key 400, http address 400, unreachable server 502 with a clear message; still Trial |
| T24 | Trial limits | **PASS** | limits: devices 30, nonit_assets 50, cloud_instances 10, technicians 1 |

New in build 92: the product is named **Zentriq Asset Intelligence** (console, installer artwork and package
description); **Re-run** for finished scans (a new scan with the same targets, exclusions, credentials and options);
agent commands on the device: `--checkin-now`, `--collect-now`, `--diagnose`, `--restart`.

| ID | Test | Result | Detail |
|---|---|---|---|
| T80 | Agent check-in and diagnosis commands | **PASS** | `--checkin-now`: Checked in (last check-in updated in the console); `--diagnose`: no problem (server key matches); `--restart`: running again |
| T34 | Scan re-run and history | **PASS** | re-run started a new scan with the same name and targets, the original kept; delete single and by age |

New in build 90: **agent tamper protection for stop and disable** — while protection is on, nobody on the device (local
or domain administrators included) can stop, disable or reconfigure the agent service, change its registry key or its
program folder; changes made behind its back are undone and reported; a watchdog task restarts it after a reboot;
`--stop`, `--disable`, `--pause <code>` with a one-hour code (device code or code for all), `--enable`; console control per
agent (pause / resume protection, restart, disable, one-hour device code). **Automatic agent updates** (optional, off by
default, limit of simultaneous updates).

| ID | Test | Result | Detail |
|---|---|---|---|
| T78 | Agent stop and disable protection | **PASS** | Services, sc.exe and the registry refused; program folder read-only; watchdog task present; wrong code refused and reported (audit + agents list); stop with the device code and `--enable`; disable with the code for all and `--enable`; protection back each time; console pause allowed Stop-Service, resume refused it again |
| T79 | Automatic agent update | **PASS** | off by default; an agent installed at the older version updated itself to 1.1.2.90 without a console request |
| T67 / T47 / T29 | Uninstall protection, console update, revocation | **PASS** | unchanged behaviour |

New in build 88: the *Cloud & Infrastructure › Add connection* dialog is laid out in numbered sections (Platform grouped
into Public cloud and Virtualization, Connection, Sign-in, Scope, Synchronisation) on one aligned two-column grid, checked
for all nine platforms at desktop and narrow widths. No API or database change.

New in build 87: **role-based access per area** (dashboard, IT inventory, non-IT inventory, IT accessories, lifecycle, cloud,
activity log, reports each with their own see / change permissions; menus, pages, dashboard widgets, report data sources and
the API all follow them), **built-in roles** (IT Asset Manager, Non-IT Asset Manager, Non-IT Asset Viewer, Asset Lifecycle
Manager, Cloud Administrator, Security Analyst), **custom roles** and **user groups**, with rights applied to signed-in users
at their next request; and **log retention and export**: audit and activity logs kept one year by default (90 days to 10
years or for ever), archived before removal, scheduled exports every 3, 6 or 12 months, export a period now, copies to a
backup destination, CSV downloads.

| ID | Test | Result | Detail |
|---|---|---|---|
| T76 | Roles and groups | **PASS** | 7 built-in roles (read-only); custom role through a group: non-IT, dashboard and reports only (12 other areas refused, report sources: nonit_assets); no privilege escalation; role in use cannot be deleted; switching the group to the viewer role applied to the live session without signing out |
| T77 | Log retention and export | **PASS** | defaults 1 year; 30 days refused; export of 30 days (audit + activity) with checksum and manifest; scheduled half-year archive made; audit and activity CSV downloads |
| T55 | API tokens | **PASS** | existing and new tokens with *Export reports* reach report data |
| — | Unit / PostgreSQL tests | **PASS** | report sources need their area's permission; built-in roles and group grants; audit entries removable only by the database owner through the purge function, never the last 90 days, removal recorded with the last hash; archives contain exactly the period with formula-safe CSV and checksums; retention cut-offs and scheduled periods |

## Build and packaging

| Step | Result |
|---|---|
| `build.ps1` (.NET SDK, NuGet audit, warnings-as-errors for vulnerable packages, web console `tsc` + Vite build) | PASS |
| `test.ps1` – unit + PostgreSQL integration tests | PASS – total=45 passed=45 failed=0 |
| `package-windows.ps1` – server publish, Zentriq Agent (.NET Framework 4.8) build, ZentriqAgent-x64.msi, Zentriq.msi (ICE validation), Burn bundle, checksums, manifest | PASS |
| Code signing | NOT RUN – no signing certificate configured; artifacts are unsigned (Windows shows "Unknown publisher" / SmartScreen "Run anyway") |

New unit/integration tests in 1.1: Windows (WMI/registry), Linux (SSH output) and SNMP inventory mapping; enrollment
code format and server key pin; agent CA issue/validate/weak-key refusal; credential encryption bound to its record;
inventory persistence against PostgreSQL (agent + agentless records of one device merge into one asset, software list
replaced) and the trial's 30-device total cap.

## Release artifacts (SHA-256)

```
8c15c803ff8b3fc15ab82f5b9081dc5fb02db3d02ba519423552bd5f1a45a9ce  Zentriq-1.1.1-docs.zip
5c31f1c8ed049f89524aebbbc2fa5b7715d18e65614dfc5070b1bd90b57680cd  Zentriq-1.1.1-msi-offline-kit.zip
01f8c9708436a15671e38adb404232b2f679f2fffc5a5abaf198175da0589711  Zentriq-Setup-x64.exe
58db92aa1b30856263f83b2c6ade65b128f6d7b84d8733da3e83b9fbbe40dd1a  Zentriq.msi
daee1c325419159263c2cb78f9729eefe2cf3a85c05f5857f54101b1b61b3a76  ZentriqAgent-x64.msi
```

License server (separate release `license-server-v1.0.0-build.3`):

```
08049bd29fe1e8bc9fa12e9fb10c948febf6d477534b458f8ebe9991066fc237  Zentriq-LicenseServer-1.0.0.zip
28b5caf2aecf9667d174793dd724c2b53836c64eee7a8ed0d41257e8c1c270f6  Zentriq-LicenseServer-Local-win-x64-1.0.0.zip
```

## Installer acceptance (tests/installer/Invoke-InstallerAcceptance.ps1)

| ID | Test | Result | Detail |
|---|---|---|---|
| T01 | First-time installation (silent, answer file) | **PASS** | exit 0 in 2 min |
| T02 | PostgreSQL provisioned as a Windows service | **PASS** | running, loopback-only |
| T05 | API and worker services running under virtual accounts | **PASS** | ready: {"status":"ready","checks":{"database":"ok","schemaVersion":4,"expectedSchemaVersion":4,"initialized":true,"worker":"ok"}} |
| T03 | Database migrations applied | **PASS** | schema version 4 |
| T06 | Trial initialized for 14 days | **PASS** | expires 10/26/2026 05:43:12 |
| T04 | First administrator can sign in | **PASS** | signed in as admin@ci.zentriq.test |
| T07 | HTTPS certificate is valid and trusted on the server | **PASS** | TLS OK, firewall rule present |
| T22 | Start menu and Desktop shortcuts use the Zentriq icon | **PASS** | Start menu + Desktop shortcuts with zentriq.ico |
| T23 | Web console sign-in page is served (not the placeholder) | **PASS** | index + /assets/index-XBtEI3Cc.js (266 KB) |
| T08 | zentriqctl verify (end-to-end health) | **PASS** | all checks passed |
| T09 | Service restart does not reset the trial | **PASS** | trial unchanged |
| T10 | Repair keeps data and trial | **PASS** | repaired; trial unchanged; sign-in works |
| T11 | Encrypted backup and deep restore validation | **PASS** | zentriq-20261012-054348-ci.zbak (32 KB) |
| T12 | Tampered/unsigned license is rejected | **PASS** | rejected with 422 |
| T24 | Trial: every module enabled, 30 devices in total | **PASS** | limits: {"devices":30} |
| T25 | Company profile can be edited and read back | **PASS** | profile saved (Zentriq CI Private Limited) |
| T26 | Agent: enrollment code, MSI download from the console, silent install, inventory received | **PASS** | runnervmtn0fv: 73 applications, 16 GB RAM, health online; MSI signature: NotSigned |
| T27 | Agent port serves only the agent API and requires a client certificate | **PASS** | / 404, /api/v1 404, no cert 401, console port 404, firewall rule present |
| T28 | Agentless WinRM inventory with a stored credential; merges with the agent record | **PASS** | 172.17.96.1 via WinRM: 73 applications; assets 1 -> 1; Inventoried runnervmtn0fv over WinRM with credential 'ci-local-admin' (73 software items). |
| T29 | Agent revocation is enforced and uninstall removes the agent | **PASS** | revoked agent refused; uninstalled cleanly |
| T13 | Upgrade to the next release preserves data and trial | **PASS** | now 1.1.1 (database reports 1.1.1); trial unchanged |
| T14 | Logs contain no administrator password or connection-string passwords | **PASS** | 39 log files scanned |
| T15 | Uninstall removes the program but keeps customer data | **PASS** | program removed; data, backups and PostgreSQL kept |
| T16 | Reinstall reuses existing data without resetting the trial | **PASS** | data reused; trial unchanged; sign-in works |
| T17 | Restart of Windows (trial persists across reboot) | **NOT RUN** | CI runners cannot reboot mid-job; covered by T09/T10/T16 persistence checks. Run manually on a VM. |
| T18 | Trial expiry enforcement end-to-end (clock moved past 14 days) | **NOT RUN** | Covered by unit tests (LicenseEvaluator); not run against an installed server. |
| T19 | Valid commercial license activation end-to-end | **NOT RUN** | Requires the vendor private key; signer + verifier covered by unit tests. Run manually with docs/licensing.md. |
| T20 | AD/OU scan in a test domain | **NOT RUN** | No Active Directory domain on CI runners. |
| T30 | Agentless SSH (Linux) and SNMP collection against real devices | **NOT RUN** | No Linux/SNMP targets on the Windows CI runner; parsers covered by unit tests. Run manually. |
| T31 | Agent over the internet through a firewall/reverse proxy | **NOT RUN** | CI runs agent and server on one machine. Run manually with a published port 8444. |
| T21 | Interactive wizard (UI) walk-through | **NOT RUN** | Silent install exercises the same provisioning; UI pages need a manual run on a VM. |

Notes:
* T28 scans the CI machine itself; for that self-scan the test sets `DisableLoopbackCheck` (NTLM refuses authentication
  to a machine's own DNS alias). An earlier run without it failed with "Access is denied", which is how this
  limitation was found; it does not apply to scanning other machines.
* T26 originally failed: the enrollment code appeared in the agent MSI log (`RegAddValue`). Fixed by writing the
  code through a hidden deferred custom action; the test now asserts the code is absent from the log.
* The trial-policy migration for existing 1.0.x installations (250/25/50/50 → 30 devices) was verified on
  PostgreSQL 16 locally (old trial row updated, immutability trigger re-enabled). Upgrading a real 1.0.2 installation
  to 1.1.0 was **not** run in CI (CI upgrades 1.1.0 → 1.1.1).

## Not yet verified

| Item | Status |
|---|---|
| Interactive wizards (server and agent MSI dialogs) on a VM | NOT RUN – silent installs exercise the same actions |
| Upgrade from the published 1.0.2 build to 1.1.0 | NOT RUN in CI (migration verified on PostgreSQL directly) |
| Agentless SSH (Linux/macOS), SNMP, Active Directory sync/scan against real systems | NOT RUN – parsers unit-tested; AD connection errors tested (T37) |
| Password reset e-mail delivery through a real SMTP server | NOT RUN |
| Setup on Windows Server 2016, and refusal on Server 2012 R2 | NOT RUN – CI has Windows Server 2022 only |
| Firewalled-host detection (ARP/ICMP) on a real LAN | NOT RUN |
| Agent over the internet / through a reverse proxy, proxy (`PROXYURL`) | NOT RUN |
| Agent on Windows 10/11 clients and Server 2016/2019 | NOT RUN – CI used Windows Server 2022 |
| Windows reboot persistence | NOT RUN |
| Trial expiry and valid commercial license end-to-end | Unit tests only |
| Signed installers / SmartScreen behaviour | NOT RUN – needs a code-signing certificate |
| Installation with an existing PostgreSQL server | NOT RUN |
