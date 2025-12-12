## Konzept
Ablauf ProcessChain Request:
- Process Chain wird bei Dispatcher angefragt (Input sind RequiredCapabilities)
    - Die Required Capabilities werden extrahiert und auf `/&lt;namespace&gt;/Offer` als Message gesendet. Typ ist `callForProposal` mit der `conversationId` aus dem ursprünglichen Product Agent. InteractionElements enthalten mindestens `Capability`, `RequirementId` und optional `ProductId`, sodass alle Planning Agents identische Informationen erhalten.
    - Der Planner Agent subscribed auf dieses Topic
    - Der Planner führt den OfferCalculationProzess durch und sendet entweder ein CapabilityOffer zurück
    - Der Dispatcher sammelt alle angebote bis entweder alle registrierten Childs ein offer oder ein refusal geschickt haben oder bis das timeout abläuft. Das Timeout ist im DispatchingAgent zu konfigurieren. 
    - Der Dispatcher baut aus den Offers eine ProcessChain zusammen
    - Wird für eine Required keine Offer gemacht wird ein refusal gesendet. 

Die CapabilityOffer ist wie folgt aufgebaut, besteht aus basyx elementen und muss im AAS-Sharp-Client erstellt werden:
{
    CapabilityOffer (SubmodelElementCollection)
        {
            OfferedCapabilityReference (ReferenceElement) -> Referenz auf gematchte Capability
            InstanceIdentifier (Property) -> Unique Identifier um das Angebot identifizieren zu können
            EarliestSchedulingInformation (Scheduling Container aus AAS-SharpClient)-> frühest möglicher freier zeitpunkt + erweartete Setup und Zykluszeit
            Station (Property) AgentId des ModuleHolons
            Cost (Property<double>) -> monetäre Bewertung des Angebots (z. B. EUR pro Auftrag)
            Actions (Action Class aus AAS-SharpClient) -> Werte aus FeasibilityCheckData (muss noch in der Action Class ergänzt werden ist aber aufgebaut wie FinalResultData), SkillReferenz,Preconditions, Title RequiredInputParameters
        }
}

Genereller Aufbau der ProcesChain: 

RequiredCapabilities (SubmodelelementList){
    RequiredCapElement{ --> keine IdShort weil in SubmodelElementList aber je angefragte RequiredCapability ein Element
        RequiredCapabilityReference (ReferenceElement) -> Referenz auf RequiredCapability des Products
        InstanceIdentifier (Property) -> Unique Identifier um das RequiredCapability eindeutig identifizieren zu können
        OfferedCapabilities (SubmodelElementList){
            Capibility Offer --> keine IdShort weil in SubmodelElementList aber je angebotene Offered Capability während des anfrageprozesses
        }
    }
}

## Vorgehen
Erweitere AAS-Sharp um die nötigen Informationsmodelle zu implementieren. Dafür müssen ergänzuungen und anpassungen gemacht werden
Implementiere anschließend die Logik im Dispatching. Achte dabei, das Verhalten in kleine, beherrschbare Nodes zu packen (Parsen → CfP senden → Angebote einsammeln → Antwort bauen → Antwort senden) und diese über den BT zu orchestrieren.
Implementiere anschließend auf der PlanningAgent Ebene eine sehr einfache Implementierung, die immer ein Angebot mit Scheduling- und Cost-Daten zurückschickt, sodass wir das Dispatching testen können. Erstelle dafür einen angepassten Planning BT, der auf `/Offer` subscribed, CfPs parsed und direkt mit einem Proposal antwortet.