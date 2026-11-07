-- Least privilege for the application account. ${APP_USER} is replaced by the installer with a validated identifier.
-- No DELETE anywhere: personal data leaves the system only through retention_pkg (owner rights, audited).
-- audit_log is INSERT + SELECT: the application can write and show the trail but never alter it.
GRANT SELECT, INSERT, UPDATE ON contact TO ${APP_USER}
/
GRANT SELECT, INSERT ON clinical_event TO ${APP_USER}
/
GRANT SELECT, INSERT, UPDATE ON survey TO ${APP_USER}
/
GRANT SELECT, INSERT, UPDATE ON outbox_message TO ${APP_USER}
/
GRANT SELECT, INSERT ON survey_response TO ${APP_USER}
/
GRANT SELECT, INSERT, UPDATE ON feedback_case TO ${APP_USER}
/
GRANT SELECT, INSERT ON case_event TO ${APP_USER}
/
GRANT SELECT, INSERT ON audit_log TO ${APP_USER}
/
GRANT SELECT ON retention_policy TO ${APP_USER}
/
GRANT SELECT ON retention_run TO ${APP_USER}
/
GRANT EXECUTE ON outbox_pkg TO ${APP_USER}
/
