# ActorRuntimeLogTextFormatter.OnEnqueueEvent method

Invoked when the specified event is about to be enqueued to an actor.

```csharp
public virtual void OnEnqueueEvent(ActorId id, Event e)
```

| parameter | description |
| --- | --- |
| id | The id of the actor that the event is being enqueued to. |
| e | The event being enqueued. |

## See Also

* class [ActorId](../ActorId.md)
* class [Event](../../Microsoft.Coyote/Event.md)
* class [ActorRuntimeLogTextFormatter](../ActorRuntimeLogTextFormatter.md)
* namespace [Microsoft.Coyote.Actors](../ActorRuntimeLogTextFormatter.md)
* assembly [Microsoft.Coyote](../../Microsoft.Coyote.md)

<!-- DO NOT EDIT: generated by xmldocmd for Microsoft.Coyote.dll -->
