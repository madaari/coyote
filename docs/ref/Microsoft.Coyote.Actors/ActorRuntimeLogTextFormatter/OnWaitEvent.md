# ActorRuntimeLogTextFormatter.OnWaitEvent method (1 of 2)

Invoked when the specified actor waits to receive an event of a specified type.

```csharp
public virtual void OnWaitEvent(ActorId id, string stateName, Type eventType)
```

| parameter | description |
| --- | --- |
| id | The id of the actor that is entering the wait state. |
| stateName | The state name, if the actor is a state machine and a state exists, else null. |
| eventType | The type of the event being waited for. |

## See Also

* class [ActorId](../ActorId.md)
* class [ActorRuntimeLogTextFormatter](../ActorRuntimeLogTextFormatter.md)
* namespace [Microsoft.Coyote.Actors](../ActorRuntimeLogTextFormatter.md)
* assembly [Microsoft.Coyote](../../Microsoft.Coyote.md)

---

# ActorRuntimeLogTextFormatter.OnWaitEvent method (2 of 2)

Invoked when the specified actor waits to receive an event of one of the specified types.

```csharp
public virtual void OnWaitEvent(ActorId id, string stateName, params Type[] eventTypes)
```

| parameter | description |
| --- | --- |
| id | The id of the actor that is entering the wait state. |
| stateName | The state name, if the actor is a state machine and a state exists, else null. |
| eventTypes | The types of the events being waited for, if any. |

## See Also

* class [ActorId](../ActorId.md)
* class [ActorRuntimeLogTextFormatter](../ActorRuntimeLogTextFormatter.md)
* namespace [Microsoft.Coyote.Actors](../ActorRuntimeLogTextFormatter.md)
* assembly [Microsoft.Coyote](../../Microsoft.Coyote.md)

<!-- DO NOT EDIT: generated by xmldocmd for Microsoft.Coyote.dll -->