-- V0025: more cloud and virtualization platforms — Google Cloud, Nutanix AHV, Proxmox VE, XCP-ng / Citrix Hypervisor,
-- oVirt / Red Hat Virtualization / Oracle Linux Virtualization Manager.
SET search_path = zentriq, public;

ALTER TABLE cloud_accounts DROP CONSTRAINT IF EXISTS cloud_accounts_provider_check;
ALTER TABLE cloud_accounts ADD CONSTRAINT cloud_accounts_provider_check
    CHECK (provider IN ('azure','aws','gcp','vmware','hyperv','nutanix','proxmox','xenserver','ovirt'));
