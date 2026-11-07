#!/bin/bash
# Runs once, on the first start of the gvenzl/oracle-free container, as the oracle OS user.
# PF_OWNER owns the schema and the retention job (DDL + CREATE JOB; used only by the installer).
# PF_APP is the account the web app connects with: it gets object grants from the installer and nothing else.
set -euo pipefail

: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER pf_owner IDENTIFIED BY "${OWNER_PASSWORD}" DEFAULT TABLESPACE users QUOTA 1G ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE, CREATE VIEW, CREATE JOB TO pf_owner;

CREATE USER pf_app IDENTIFIED BY "${APP_PASSWORD}";
GRANT CREATE SESSION TO pf_app;
EXIT
SQL
