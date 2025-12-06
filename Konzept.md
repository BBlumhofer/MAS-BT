Das Verhalten jedes Agenten (Ressource, Transport, Produkt, Modul) wird über Behavior Trees orchestriert.  
Die **Monitoring- und Constraint-Überprüfung** geschieht ebenfalls über Behavior-Tree-Nodes, sodass alles deterministisch, wiederverwendbar und modular bleibt.
## **1. Modul-Monitoring via OPC UA**

Die bisherigen Zustände eines Moduls (Maschine, Station, Fertigungseinheit) werden nun über **OPC UA Nodes** abgefragt, anstatt nur über AAS oder MQTT. OPC UA erlaubt Zugriff auf aktuelle Werte, Alarme und Historie.

### MQTT Struktur Execution Agent:
| Submodel / Kategorie | Node-Name (OPC UA)                     | Wert / Typ        |
| -------------------- | -------------------------------------- | ----------------- |
| Resource State       | /Modules/$ModuleID$/State              | StateSummary      |
|                      | /Modules/$ModuleID$/State/Notification | Integer / String  |
| Inventory            | /Modules/$ModuleID$/Inventory/         | Inventory Message |
| Neighbor             | /Modules/$ModuleID$/Neighbors/         | Neighbor  Message |
| Skill                | /Modules/$ModuleID$/SkillRequest/      | SkillRequest      |
|                      | /Modules/$ModuleID$/SkillUpdate/       | SkillResponse     |
| Log                  | /Modules/$ModuleID$/Log/               | LogMessage        |

---
## Agenten:
- Jedes Modul hat standardmäßig zwei Agenten: Einen Execution Agent und einen Planning Agent mit eigenen BehaviorTrees
- der Execution Agent hat die oben genannten MQTT Topics Er komuniziert mit dem Planungsagenten über SkillRequest und SkillResponse die auszuführenden Actionen
- Der Planungsagent übernimmt sowohl Scheduling als auch angebotsverwaltung etc. Der Execution Agent kümmert sich nur um das Checken von Preconditions und die eigentliche Ausführung Als Interactionselement wird über SkillRequest und SkillResponse ein Action Element ausgetauscht. 
- Die Agenten sind aus Behavior Trees zusammgestellt. Für größere Verhalten wie  der Lifecycle States werden SubTrees erstellt und geladen. Erstelle im Subtrees Ordner Subtrees für kleiner Subverhalten, die wiederverwendet werden können.

## **2. Behavior Tree Node-Bibliothek Ressourcenagent**

### **Monitoring Nodes (OPC UA-fähig)**

- `CheckReadyState(opcNode)` → prüft `/State/isReady`
- `CheckLockedState(opcNode, expectLocked)` → prüft `/State/isLocked`
- `CheckErrorState(opcNode, expectedError=false)` → prüft `/State/errorCode`
- `CheckToolAvailability(toolId, ModuleID)` → prüft ob Tool im Inventar ist
- `CheckInventory(itemId, minAmount, ModuleID)` → prüft ob Item im Inventar ist
- `RefreshStateMessage(itemId, minAmount, ModuleID)` → prüft ob Item im Inventar ist
- `CheckAlarmHistory(alarmType, timeRange)` → liest AlarmHistorie und Notifications

### **Constraint Nodes**

- `RequiresTool(toolId, ModuleID)` → integriert Tool-Constraints
- `RequiresMaterial(itemId, quantity, ModuleID)` → Material-Constraints
- `ProductMatchesOrder(expectedProduct, ModuleID)` → Produkt-Constraints
- `ProcessParametersValid(paramConstraints, ModuleID)` → Prozess-Constraints
- `ModuleReady(ModuleID)` → Prozess-Constraints
- `ResourceAvailable(ModuleID)` → Darf der prozess ausgeführt werden?
- `SafetyOkay(zoneId, ModuleID)` → Sicherheits-Constraints werden überprüft
- `RequireNeighborAvailable(neighborId)` Prüft, ob Nachbar nicht locked, not busy ist.
- `CheckTransportArrival(transportRequestId)` Prüft, ankunftszeit von Transport
- `CheckCurrentSchedule(Schedule)` Prüft, ob aktueller Arbeitsplan mir Transporten übereinstimmt
- `CheckEarliestStartTime(taskId, timestamp)`
- `CheckDeadlineFeasible(taskId, deadline)`
- `CheckModuleCapacity(moduleId, requiredCapacity)`

### **Skill Nodes**

- `WaitForSkillState(skillName, SkillStatesEnum,timeout)` → warte auf spezifischen SkillZustand
- `ExecuteSkill(skillName, skillParams)` → ruft Skill via OPC UA / C# Client auf
- `AbortSkill(skillName)` setzt Skill auf Halting
- `PauseSkill(skillName)` setzt Skill auf Suspended
- `ResumeSkill(skillName)` setzt Skill von Suspended auf Running
- `ManualCompleteSkill(skillName)` Markiert Skill aus dem Schedule als completed ohne etwas auszuführen
- `RetrySkill(skillName)` versucht skill erneut zu starten
- `AbortSkill(skillName, skillParams)` → Bricht Skill ab und löscht ihn aus dem Schedule
- `MonitoringSkill(skillName)` → Liest Monitoring Variablen von Skill und Komponente sowie den Zustand
- `UpdateInventory(skillName)` → aktualisiert den Lagerbestand des Moduls
- `UpdateNeighbors(skillName)` → aktualisiert gekoppelte Nachbar
- `FeedbackSkill(skillName)` → liest Rückmeldungen / Status von Skills
- `ExecuteFeasibilityCheck(skillName,skillParams)` → Führt FeasibilityChecks aus
- `FeedbackFeasibilityCheck(skillName)` → Liest ergebnisse / Statuis des Feasibility Checks aus
-  `DetermineSkillParameters(skillName, productContext)`->Berechnet Skill-Parameter dynamisch aus dem Produkt-/Prozesskontext.


### **Planning Nodes
- `ExecuteCapabilityMatchmaking(RequierdCapability)` → Analysiert ob Capability ausgeführt werden könnte
- `FeedbackCapabilityMatchmaking(RequierdCapability)` → Verarbeitet Ergebnisse aus dem Capability Matchmaking
- `SchedulingExecute(RequierdCapability)` → Führt Scheduling algorithmus aus
- `SchedulingFeedback(RequierdCapability)` →Bewertet Scheduling Ergebnisse
- `CalculateOffer(RequierdCapability)` → Berechnet Angebot
- `SendOffer(RequierdCapability)` → Sendet Angebot ab
- `ReceiverRequestForOffer(RequierdCapability)` → Verarbeitet Angebotsanfragen
- `UpdateMachineSchedule(RequierdCapability)` → Aktualisiert MaschineSchedule auf Basis von Angebotszuständen und aktuellem Schedule
- `RequestTransport(RequierdCapability)` → Fragt Transporte an
- `EvaluateRequestTransportResponse(RequierdCapability)` → Bewertet die Rückmeldung für die Transportanfragen
- `CheckScheduleFeasibility(Schedule, currentMachineState)` → Bewertet die Rückmeldung für die Transportanfragen
- `OnTransportArrived(transportId)`
- `ReadCapabilityDescription(transportId)`
- `OnTransportArrived(transportId)`
### **Configuration**

- `ConnectToModule(endpoint)`
- `ReadCapabilityDescriptionSM(AgentId)`
- `ReadNameplateSM(AgentId)`
- `ReadSkillsSM(AgentId)``
- `ReadMachineSchedule(AgentId)``
- `ReadShell(AgentId)`
- `CoupleModule(ModuleId)`


### **Locking Nodes (OPC UA + MQTT hybrid)**

- `LockResource(resourceId)` → schreibt OPC UA Node `/State/isLocked = true` und optional MQTT Publish
- `UnlockResource(resourceId)` → `/State/isLocked = false`
- `WaitUntilUnlocked(resourceId, timeout)` → Polling auf OPC UA Node
- `CheckLockOwner(resourceId)`

### **Event / Subscription Nodes**

- `OnNodeChanged(opcNode, callback)` → BT reagiert auf Änderungen von OPC UA Nodes
- `EventSelector([opcNodeCallbacks])` → parallel auf mehrere Events reagieren
- `OnSkillStateChanged(skill, state)`
- `OnInventoryChanged(itemId)`
- `OnNeighborChanged(moduleId)
### **Messaging**
- `SendMessage(AgentId, Payload)
- `WaitForMessage(type, timeout)
- `ReadMqttSkillRequest()` → Liest anstehende Anfragen zur Skillausführung aus
- `ReceiveOfferMessage(RequierdCapability)` → Empfängt Rückmeldung zu Angebot und setzt zustände der Angebote/löscht abgelehnte
---
## **3. Behavior Tree Node-Bibliothek ProductAgent**

### **Configuration**

- `InitProductHolon(aasId)` Lädt Produktidentifikation, BOM, Konfiguration, Szenario.
- `LoadRequiredCapabilities(aasId)` Lädt RequiredCapabilities
- `LoadBillOfMaterial(aasId)` Extrahiert Kinder aus dem AAS-Modell.
- `SpawnChildHolons(aasId)` Erstellt dynamische Produkt-Holon-Kinder
- `WaitForChildPlanInitialization(aasId)` Blockiert, bis alle Kinder ihre Pläne generiert haben.

### **Monitoring**
- `MonitorProductHolonState(intervalMs)` Periodisches Monitoring der eigenen Zustände.
- `CheckChildHolonAlive(childId)` Überwacht Kind-Agenten (child_holon_states).
- `RestartFailedChild(childId)` Implementiert deine spawn_retry Logik.
- `IsExecutionAllowed()` Entspricht `allow_execution()` → prüft:  (1) Keine lebenden Kinder  (2) Alle Child-Pläne verfügbar
- `TerminateProductHolon(reason)` Kill-Sequenz je nach Szenario (Plan done, aborted etc).
- `CheckChildPlansComplete()` Überprüft, ob alle Kindprodukte fertig sind.
- `AwaitChildProductionCompletion()` Wartet auf die Schritte der Kinder

### **Execution**
- `FetchNextProductionStep(ProcessChain)` GetCurrentStep()
- `IsStepRunning(step)`überprüft aktuellen Zustand des Steps
### **Planning**

- RequestManufcaturingSequencesGeneration(RequiredCapabilities)
- SelectManufacturingSequence(ManufacturingSequences)
- DeriveProductionPlan(ManufacturingSequence)
- RescheduleProductionPlan(ProductionPlan)
- RescheduleStep(Step) --> eingeplanten Schritt Schedulen
- ReplanStep(Step) --> Schritt neu verteilen und einplanen
- AskForStepExecution(Step) --> Schritt des Produktionsplans bei den Ressourcen anfragen für die Ausführung
- AskForStepsExecution(Step)) --> Mehrere Schritte des Produktionsplans bei den Ressourcen anfragen für die Ausführung
- AskForStepSchedulingAndExecution(Step)--> Ein Schritt des Produktionsplans bei den Ressourcen anfragen für die Terminierung und Ausführung
- AskForStepsSchedulingAndExecution(Step) --> Mehrere Schritte des Produktionsplans bei den Ressourcen anfragen für die Terminierung und Ausführung
- HandleCompletedStep
- HandleAbortedStep
- UpdateStepState
- FinishPlan
- KillAfterPlanCompletion

## **7. MessageTypes**

StateSummary:    
{
"frame": {
        "sender": {
            "identification": {
                "id": "Module2_Planning_Agent"
            },
            "role": {
            }
        },
        "receiver": {
            "identification": {
                "id": "Module2_Execution_Agent"
            },
            "role": {
            }
        },
        "type": "request",
        "conversationId": "https://smartfactory.de/shells/_5XYUiVv2B"
    },
    "interactionElements": [
        SubmodelElementCollection State mit Properties: ModuleLocked (bool), StartupSkill running (bool),  ModuleReady (bool), ModuleState (LifecycleStateEnum)
    ]
}

InventoryMessage (hier nicht I4.0 Language konform):
 [
    {
      "name": "Storage",
      "slots": [
        {
          "index": 0,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": false
          }
        },
        {
          "index": 1,
          "content": {
            "CarrierID": "",
            "CarrierType": "WST_C",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": false
          }
        },
        {
          "index": 2,
          "content": {
            "CarrierID": "WST_A_13",
            "CarrierType": "",
            "ProductType": "Semitrailer_Truck",
            "ProductID": "https://smartfactory.de/shells/JNmavC69s3",
            "IsSlotEmpty": false
          }
        },
        {
          "index": 3,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": false
          }
        },
        {
          "index": 4,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": true
          }
        },
        {
          "index": 5,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": true
          }
        }
      ]
    },
    {
      "name": "RFIDStorage",
      "slots": [
        {
          "index": 0,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": true
          }
        },
        {
          "index": 1,
          "content": {
            "CarrierID": "",
            "CarrierType": "",
            "ProductType": "",
            "ProductID": "",
            "IsSlotEmpty": true
          }
        }
      ]
    }
  ]
}

SkillRequest:{
    "frame": {
        "sender": {
            "identification": {
                "id": "Module2_Planning_Agent"
            },
            "role": {
            }
        },
        "receiver": {
            "identification": {
                "id": "Module2_Execution_Agent"
            },
            "role": {
            }
        },
        "type": "request",
        "conversationId": "https://smartfactory.de/shells/_5XYUiVv2B"
    },
    "interactionElements": [
        {
            "modelType": "SubmodelElementCollection",
            "semanticId": {
                "keys": [
                    {
                        "type": "GlobalReference",
                        "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action"
                    }
                ],
                "type": "ExternalReference"
            },
            "idShort": "Action001",
            "value": [
                {
                    "modelType": "Property",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/ActionTitle"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "value": "RetrieveToPortLogistic",
                    "valueType": "xs:string",
                    "idShort": "ActionTitle"
                },
                {
                    "modelType": "Property",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Status"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "value": "planned",
                    "valueType": "xs:string",
                    "idShort": "Status"
                },
                {
                    "modelType": "SubmodelElementCollection",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/InputParameters"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "idShort": "InputParameters",
                    "value": [
                        {
                            "modelType": "Property",
                            "value": "true",
                            "valueType": "xs:string",
                            "idShort": "RetrieveByProductType"
                        },
                        {
                            "modelType": "Property",
                            "value": "Cab_A_Blue",
                            "valueType": "xs:string",
                            "idShort": "ProductType"
                        },
                        {
                            "modelType": "Property",
                            "value": "",
                            "valueType": "xs:string",
                            "idShort": "ProductID"
                        }
                    ]
                },
                {
                    "modelType": "SubmodelElementCollection",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Preconditions"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "idShort": "Preconditions"
                },
                {
                    "modelType": "Property",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/MachineName"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "value": "Module_2",
                    "valueType": "xs:string",
                    "idShort": "MachineName"
                }
            ]
        }
    ]
}

SkillResponse:
{
    "frame": {
        "sender": {
            "identification": {
                "id": "Module2_Planning_Agent"
            },
            "role": {
            }
        },
        "receiver": {
            "identification": {
                "id": "Module2_Execution_Agent"
            },
            "role": {
            }
        },
        "type": "consent",
        "conversationId": "https://smartfactory.de/shells/_5XYUiVv2B"
    },
    "interactionElements": [
                {
                    "modelType": "Property",
                    "semanticId": {
                        "keys": [
                            {
                                "type": "GlobalReference",
                                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/ActionState"
                            }
                        ],
                        "type": "ExternalReference"
                    },
                    "value": "starting",
                    "valueType": "xs:string",
                    "idShort": "ActionState"
                }
    ]
}

Action State kann: Halted, Ready, Starting, Running, Halting, Halted, Completing, Completed, Suspending, Suspended sein

SkillResponse:
{
    "frame": {
        "sender": {
            "identification": {
                "id": "Module2_Planning_Agent"
            },
            "role": {
            }
        },
        "receiver": {
            "identification": {
                "id": "Module2_Execution_Agent"
            },
            "role": {
            }
        },
        "type": "update",
        "conversationId": "https://smartfactory.de/shells/_5XYUiVv2B"
    },
    "interactionElements": [
					{
							"modelType": "SubmodelElementCollection",
							"semanticId": {
								"keys": [
									{
										"type": "GlobalReference",
										"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action"
									}
								],
								"type": "ExternalReference"
							},
							"idShort": "Action001",
							"value": [
								{
									"modelType": "Property",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/ActionTitle"
											}
										],
										"type": "ExternalReference"
									},
									"value": "RetrieveToPortLogistic",
									"valueType": "xs:string",
									"idShort": "ActionTitle"
								},
								{
									"modelType": "Property",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Status"
											}
										],
										"type": "ExternalReference"
									},
									"value": "done",
									"valueType": "xs:string",
									"idShort": "Status"
								},
								{
									"modelType": "SubmodelElementCollection",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/InputParameters"
											}
										],
										"type": "ExternalReference"
									},
									"idShort": "InputParameters",
									"value": [
										{
											"modelType": "Property",
											"value": "true",
											"valueType": "xs:string",
											"idShort": "RetrieveByProductType"
										},
										{
											"modelType": "Property",
											"value": "Cab_A_Blue",
											"valueType": "xs:string",
											"idShort": "ProductType"
										},
										{
											"modelType": "Property",
											"value": "",
											"valueType": "xs:string",
											"idShort": "ProductID"
										}
									]
								},
								{
									"modelType": "SubmodelElementCollection",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/FinalResultData"
											}
										],
										"type": "ExternalReference"
									},
									"idShort": "FinalResultData"
								},
								{
									"modelType": "SubmodelElementCollection",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Preconditions"
											}
										],
										"type": "ExternalReference"
									},
									"idShort": "Preconditions"
								},
								{
									"modelType": "ReferenceElement",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/SkillReference"
											}
										],
										"type": "ExternalReference"
									},
									"idShort": "SkillReference",
									"value": {
										"keys": [
											{
												"type": "Submodel",
												"value": "https://example.com/ids/sm/4510_5181_3022_5180"
											},
											{
												"type": "SubmodelElementCollection",
												"value": "Skills"
											},
											{
												"type": "SubmodelElementCollection",
												"value": "Skill_0001"
											}
										],
										"type": "ModelReference"
									}
								},
								{
									"modelType": "SubmodelElementCollection",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Effecs"
											}
										],
										"type": "ExternalReference"
									},
									"idShort": "Effects"
								},
								{
									"modelType": "Property",
									"semanticId": {
										"keys": [
											{
												"type": "GlobalReference",
												"value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/MachineName"
											}
										],
										"type": "ExternalReference"
									},
									"value": "StorageModule",
									"valueType": "xs:string",
									"idShort": "MachineName"
								}
							]
						}
	
    ]
}
LogMessage:
{
  "frame": {
    "sender": {
      "identification": {
        "id": "TestAgent"
      },
      "role": {
        "name": ""
      }
    },
    "receiver": {
      "identification": {
        "id": "broadcast"
      },
      "role": {
        "name": ""
      }
    },
    "type": "inform",
    "conversationId": "0a75ae7c-d64f-4221-91b0-85047a06e85f",
    "messageId": "dbbb5317-f342-45af-ad89-1e469cd29018"
  },
  "interactionElements": [
    {
      "value": "INFO",
      "valueType": "xs:string",
      "modelType": "Property",
      "idShort": "LogLevel"
    },
    {
      "value": "Screw skill FinalResultData: {Context.ScrewSkillResult}",
      "valueType": "xs:string",
      "modelType": "Property",
      "idShort": "Message"
    },
    {
      "value": "2025-12-05T23:56:28.0080838Z",
      "valueType": "xs:dateTime",
      "modelType": "Property",
      "idShort": "Timestamp"
    },
    {
      "value": "ResourceHolon",
      "valueType": "xs:string",
      "modelType": "Property",
      "idShort": "AgentRole"
    }
  ]
}


NeighborMessage:
{
    "frame": {
        "sender": {
            "identification": {
                "id": "Module2_Execution_Agent"
            },
            "role": {
            }
        },
        "receiver": {
            "identification": {
                "id": ""
            },
            "role": {
            }
        },
        "type": "consent",
        "conversationId": "https://smartfactory.de/shells/_5XYUiVv2B"
    },
    "interactionElements": [
                {
                        "idShort": "Neighbors",
                        "typeValueListElement": "Property",
                        "valueTypeListElement": "xs:string",
                        "value": [
                          {
                            "valueType": "xs:string",
                            "value": "Module1",
                            "modelType": "Property"
                          },
                          {
                            "valueType": "xs:string",
                            "value": "Module3",
                            "modelType": "Property"
                          }
                        ],
                        "modelType": "SubmodelElementList"
                      }

    ]
}


here are 4 primary states:

Unconfigured
Inactive
Active
Finalized
To transition out of a primary state requires action from an external supervisory process, with the exception of an error being triggered in the Active state.

There are also 6 transition states which are intermediate states during a requested transition.

Configuring
CleaningUp
ShuttingDown
Activating
Deactivating
ErrorProcessing
In the transitions states logic will be executed to determine if the transition is successful. Success or failure shall be communicated to lifecycle management software through the lifecycle management interface.

There are 7 transitions exposed to a supervisory process, they are:

create
configure
cleanup
activate
deactivate
shutdown
destroy