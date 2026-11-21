# Deferred ideas

Ideas recorded here are not committed implementation work. Revisit them when
their prerequisites are understood and their scope is agreed.

## DI-01: Komens message hierarchy (original messages and replies)

**Status:** Deferred pending verification of API response metadata.

**Idea:** Group messages into conversations and distinguish original messages
from replies using explicit links supplied by Bakalari, if available.

### What is known

The community API documentation includes `PreviousMessageId` in the payload for
`POST /api/3/komens/message`. This appears to identify the preceding message when
sending a reply, suggesting that Bakalari can maintain a message relationship.
The documented example sets it to `null`; it does not establish the precise
semantics of a populated value.

However, the documented received/sent message lists and individual message
detail responses do not show `PreviousMessageId` or a thread identifier.
`CanAnswer` only indicates whether replying is allowed, not which message is
being answered. These references are community-maintained, not an official
compatibility guarantee.

### Why this is deferred

Support for linking a reply when sending does not prove that a read-only client
can retrieve that link. OBK needs evidence from actual API responses before it
can reliably reconstruct an original/reply hierarchy.

Matching subjects, participants, timestamps, or quoted message text could
suggest related messages, but would be heuristic. Such matches must not be
presented as confirmed parent/reply relationships.

### Investigation before implementation

- Inspect received and sent list/detail responses for a known original/reply
  pair, without sending messages or changing server-side message state.
- Check for `PreviousMessageId` or another explicit relationship field, and
  verify whether it references the immediate parent or the conversation root.
- Check nested replies, missing parents, and links across received/sent folders.
- If reliable metadata exists, agree the offline storage and conversation UI
  scope separately. Otherwise, keep messages independent unless an explicitly
  labeled heuristic grouping approach is agreed.

Do not commit private message payloads or credentials as investigation examples.
Sending messages remains outside OBK's scope.

### References

- [Sending a Komens message: payload containing PreviousMessageId](https://github.com/bakalari-api/bakalari-api-v3/blob/master/moduly/komens_send.md)
- [Komens message lists and details](https://github.com/bakalari-api/bakalari-api-v3/blob/master/moduly/komens.md)
