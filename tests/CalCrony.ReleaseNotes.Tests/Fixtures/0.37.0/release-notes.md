### What's new

- **Free slots.** `/availability slots` lists the times a member, or everyone in a role, is free over the next 1 to 14 days.
- **Available hours.** Each member can save the hours they are usually free with `/settings available-hours`, and free slots stay inside them.

### How to use it

1. **GM:** link your calendar with `/calendar connect` and set your zone with `/settings timezone`.
2. **GM:** save your hours, for example `/settings available-hours set days:weekdays from:6pm to:11pm`.
3. **Players:** run `/availability slots who:@GM` to see when the GM is free.
4. Add `days:14`, `when:saturday` or `duration:180` to look further ahead, start on another day or ask for longer slots.

---

## [0.37.0](https://github.com/JJWren/CalCrony/compare/v0.36.4...v0.37.0) (2026-11-13)


### Features

* free slots for a member or role (/availability slots) ([#172](https://github.com/JJWren/CalCrony/issues/172)) ([a079441](https://github.com/JJWren/CalCrony/commit/a079441a5d704cfb07fa71e02b44be73fec3eeb4)), closes [#171](https://github.com/JJWren/CalCrony/issues/171)
* per-member available hours for free slots ([#177](https://github.com/JJWren/CalCrony/issues/177)) ([b5b90d6](https://github.com/JJWren/CalCrony/commit/b5b90d610e652a4b40c2ceb07552111bda04afb2)), closes [#171](https://github.com/JJWren/CalCrony/issues/171)
