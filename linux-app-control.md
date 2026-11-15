# Zentriq application control on Linux (containers, x64)

| ID | Distribution | Result | Checks |
|---|---|---|---|
| A01 | Ubuntu 24.04 (apt / dpkg) | **PASS** | approved package installed: OK;unapproved package refused with the message: OK;request by a normal user: OK;audit mode: installed and reported: OK;hooks removed with the agent: OK; |
| A02 | Debian 12 (dpkg -i) | **PASS** | approved package installed: OK;unapproved package refused with the message: OK;request by a normal user: OK;audit mode: installed and reported: OK;hooks removed with the agent: OK; |
| A03 | Rocky Linux 9 (dnf) | **PASS** | approved package installed: OK;unapproved package refused with the message: OK;request by a normal user: OK;audit mode: installed and reported: OK;hooks removed with the agent: OK; |
| A04 | Amazon Linux 2023 (dnf) | **PASS** | approved package installed: OK;unapproved package refused with the message: OK;request by a normal user: OK;audit mode: installed and reported: OK;hooks removed with the agent: OK; |
| A05 | openSUSE Leap 15.6 (zypper: reported, not stopped) | **PASS** | approved package installed: OK;unapproved package reported (zypper cannot be stopped from a plugin): OK;request by a normal user: OK;audit mode: installed and reported: OK;hooks removed with the agent: OK; |
