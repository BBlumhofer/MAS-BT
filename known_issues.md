## Known issues

- Orchestrator halts and restarts startup after relock instead of just leaving it running

- MQTT topic mismatch (runtime observation): Test publisher sent SkillRequest an Topic `/Modules/Module2/SkillRequest/`, während der Execution-Agent auf `/Modules/CA-Module/SkillRequest/` (bzw. `config.Agent.ModuleId`) subscribed war. Folge: keine Verarbeitung eingehender Requests.
	- Workaround: Entweder Publisher-Topic an `config.Agent.ModuleId` anpassen oder in der Agent-Konfiguration `config.Agent.ModuleId` auf den publizierten Modul-Namen setzen.

- `BadInvalidState` beim Starten von `CoupleSkill` in einigen Laufzeitfällen.
	- Status: `RemotePort.CoupleAsync` versucht `Reset` gefolgt von `Start` als Recovery; wenn das nicht hilft, sollte Timeout/Retry-Policy oder Skill-State-Überwachung erweitert werden.
