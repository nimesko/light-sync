//! What every LED should show: a default state, per-target overrides, groups and fan layouts.
//!
//! Target ids:
//! - device: `"<provider>:<name>"` (stable across OpenRGB restarts; `#2`, `#3`... for duplicate names)
//! - zone:   `"<device id>/<zone name>"` (see [`zone_ids`]; older files used the zone index)
//! - fan:    `"<zone id>/<fan index>"` (only for zones with a fan layout)
//!
//! The most specific override wins: fan > zone > device > default. Effects run across the scope the
//! state was set on (a rainbow set on a fan spans that fan; set on a device it spans the device).

use crate::effects::LightState;
use crate::triggers::Trigger;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Group {
    pub id: String,
    pub name: String,
    pub members: Vec<String>,
}

/// How many fans are chained on an addressable header, and LEDs per fan.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct FanLayout {
    pub fans: u32,
    pub leds_per_fan: u32,
}

impl FanLayout {
    pub fn total(&self) -> u32 {
        self.fans * self.leds_per_fan
    }
}

/// The lighting part of a scene (what a profile saves): no groups, layouts or power state.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct Lighting {
    pub default: LightState,
    pub overrides: BTreeMap<String, LightState>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct Scene {
    pub default: LightState,
    pub overrides: BTreeMap<String, LightState>,
    pub groups: Vec<Group>,
    /// zone id -> fan layout
    pub layouts: BTreeMap<String, FanLayout>,
    /// All lights off (black) without forgetting the scene.
    pub off: bool,
    /// Saved lighting, by profile name.
    pub profiles: BTreeMap<String, Lighting>,
    /// Device ids LightSync never writes to (their own software or OpenRGB keeps them).
    pub hidden: BTreeSet<String>,
    /// Reactive triggers (see triggers.rs); none by default.
    pub triggers: Vec<Trigger>,
}

/// Zone ids for one device, by zone name so they survive OpenRGB reordering zones between
/// versions. `/` is replaced, names that are only digits get a `zone-` prefix (so they never look
/// like an old index id), and duplicate names get `#2`, `#3`...
pub fn zone_ids<'a>(dev: &str, names: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut seen: BTreeMap<String, u32> = BTreeMap::new();
    names
        .into_iter()
        .map(|n| {
            let mut slug = n.trim().replace('/', "-");
            if slug.chars().all(|c| c.is_ascii_digit()) {
                slug = format!("zone-{slug}");
            }
            let k = seen.entry(slug.clone()).or_insert(0);
            *k += 1;
            if *k == 1 {
                format!("{dev}/{slug}")
            } else {
                format!("{dev}/{slug}#{k}")
            }
        })
        .collect()
}

/// Move keys saved under `old` (and its children, `old/...`) to `new`. Keys already under `new` win.
fn rename_keys<V>(m: &mut BTreeMap<String, V>, old: &str, new: &str) -> bool {
    let child = format!("{old}/");
    let keys: Vec<String> = m
        .keys()
        .filter(|k| *k == old || k.starts_with(&child))
        .cloned()
        .collect();
    for k in &keys {
        let v = m.remove(k).expect("key listed above");
        m.entry(format!("{new}{}", &k[old.len()..])).or_insert(v);
    }
    !keys.is_empty()
}

/// One LED's state and its position inside the scope that state applies to.
pub struct Pick<'a> {
    pub state: &'a LightState,
    pub pos: usize,
    pub len: usize,
}

impl Scene {
    /// Parse a saved scene; older files held just one `LightState`.
    pub fn from_json(s: &str) -> Option<Scene> {
        serde_json::from_str::<Scene>(s)
            .ok()
            .filter(|sc| {
                s.contains("\"default\"") || !sc.overrides.is_empty() || !sc.groups.is_empty()
            })
            .or_else(|| {
                serde_json::from_str::<LightState>(s)
                    .ok()
                    .map(|default| Scene {
                        default,
                        ..Default::default()
                    })
            })
    }

    /// The state a target currently shows (its own override or the closest parent's).
    pub fn effective(&self, target: &str) -> &LightState {
        let mut t = target;
        loop {
            if let Some(s) = self.overrides.get(t) {
                return s;
            }
            match t.rfind('/') {
                Some(i) => t = &t[..i],
                None => return &self.default,
            }
        }
    }

    /// Apply `next` to one target: "all" resets everything to it; otherwise the target gets an
    /// override and its children lose theirs (so the whole target shows the new state).
    pub fn apply(&mut self, target: &str, next: LightState) {
        if target == "all" || target.is_empty() {
            self.default = next;
            self.overrides.clear();
            return;
        }
        let prefix = format!("{target}/");
        self.overrides.retain(|k, _| !k.starts_with(&prefix));
        self.overrides.insert(target.to_string(), next);
    }

    pub fn group(&self, id: &str) -> Option<&Group> {
        self.groups.iter().find(|g| g.id == id)
    }

    /// Members a target id stands for ("group:<id>" expands to its members).
    pub fn expand(&self, target: &str) -> Vec<String> {
        match target.strip_prefix("group:") {
            Some(id) => self
                .group(id)
                .map(|g| g.members.clone())
                .unwrap_or_default(),
            None => vec![target.to_string()],
        }
    }

    pub fn next_group_id(&self) -> String {
        let n = self
            .groups
            .iter()
            .filter_map(|g| g.id.strip_prefix('g')?.parse::<u32>().ok())
            .max()
            .unwrap_or(0);
        format!("g{}", n + 1)
    }

    /// Resolve LED `j` of zone `zone` (`zone_len` LEDs) of device `dev`, which is LED `offset + j`
    /// of the device's `dev_len` LEDs.
    pub fn pick<'a>(
        &'a self,
        dev: &str,
        zone: &str,
        j: usize,
        zone_len: usize,
        offset: usize,
        dev_len: usize,
    ) -> Pick<'a> {
        if let Some(l) = self
            .layouts
            .get(zone)
            .filter(|l| l.fans > 0 && l.leds_per_fan > 0)
        {
            let per = l.leds_per_fan as usize;
            let k = j / per;
            if k < l.fans as usize {
                if let Some(s) = self.overrides.get(&format!("{zone}/{k}")) {
                    return Pick {
                        state: s,
                        pos: j - k * per,
                        len: per,
                    };
                }
            }
        }
        if let Some(s) = self.overrides.get(zone) {
            return Pick {
                state: s,
                pos: j,
                len: zone_len,
            };
        }
        Pick {
            state: self.overrides.get(dev).unwrap_or(&self.default),
            pos: offset + j,
            len: dev_len,
        }
    }

    /// Whether anything visible is animated (static scenes are only re-sent as keepalive).
    pub fn is_animated(&self) -> bool {
        !self.off
            && (self.default.effect.is_animated()
                || self.overrides.values().any(|s| s.effect.is_animated()))
    }

    pub fn lighting(&self) -> Lighting {
        Lighting {
            default: self.default.clone(),
            overrides: self.overrides.clone(),
        }
    }

    /// Move everything saved for target `old` (overrides, fan layouts, group members, trigger
    /// targets, profiles) to `new`. Returns whether anything moved.
    pub fn rename_target(&mut self, old: &str, new: &str) -> bool {
        let mut moved = rename_keys(&mut self.overrides, old, new);
        moved |= rename_keys(&mut self.layouts, old, new);
        for p in self.profiles.values_mut() {
            moved |= rename_keys(&mut p.overrides, old, new);
        }
        let child = format!("{old}/");
        let members = self
            .groups
            .iter_mut()
            .flat_map(|g| g.members.iter_mut())
            .chain(self.triggers.iter_mut().flat_map(|t| t.targets.iter_mut()));
        for m in members {
            if *m == old || m.starts_with(&child) {
                *m = format!("{new}{}", &m[old.len()..]);
                moved = true;
            }
        }
        moved
    }

    /// Whether LightSync leaves this device alone.
    pub fn is_hidden(&self, device: &str) -> bool {
        self.hidden.contains(device)
    }

    /// Show a saved profile (and turn the lights on).
    pub fn load(&mut self, l: &Lighting) {
        self.default = l.default.clone();
        self.overrides = l.overrides.clone();
        self.off = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::effects::Effect;

    fn st(color: &str) -> LightState {
        LightState {
            color: color.into(),
            ..Default::default()
        }
    }

    #[test]
    fn most_specific_override_wins() {
        let mut s = Scene::default();
        s.layouts.insert(
            "openrgb:Board/0".into(),
            FanLayout {
                fans: 3,
                leds_per_fan: 4,
            },
        );
        s.apply("openrgb:Board", st("00ff00"));
        s.overrides.insert("openrgb:Board/0/1".into(), st("0000ff"));
        let pick = |j| s.pick("openrgb:Board", "openrgb:Board/0", j, 12, 2, 20);
        assert_eq!(pick(0).state.color, "00ff00");
        assert_eq!((pick(0).pos, pick(0).len), (2, 20));
        assert_eq!(pick(5).state.color, "0000ff");
        assert_eq!((pick(5).pos, pick(5).len), (1, 4));
        assert_eq!(s.effective("openrgb:Board/0/2").color, "00ff00");
        assert_eq!(s.effective("openrgb:Mouse").color, "ffffff");
    }

    #[test]
    fn apply_clears_children_and_all_resets() {
        let mut s = Scene::default();
        s.apply("d/0/1", st("111111"));
        s.apply("d/0", st("222222"));
        assert!(!s.overrides.contains_key("d/0/1"));
        s.apply("d", st("333333"));
        assert_eq!(s.overrides.len(), 1);
        s.apply("all", st("444444"));
        assert!(s.overrides.is_empty());
        assert_eq!(s.default.color, "444444");
    }

    #[test]
    fn groups_expand_and_ids_increase() {
        let mut s = Scene::default();
        assert_eq!(s.next_group_id(), "g1");
        s.groups.push(Group {
            id: "g1".into(),
            name: "Fans".into(),
            members: vec!["a".into(), "b/0".into()],
        });
        assert_eq!(s.next_group_id(), "g2");
        assert_eq!(s.expand("group:g1"), vec!["a", "b/0"]);
        assert_eq!(s.expand("a"), vec!["a"]);
        assert!(s.expand("group:nope").is_empty());
    }

    #[test]
    fn old_state_files_still_load() {
        let old = r#"{"effect":"breathing","color":"123456","brightness":40,"speed":10}"#;
        let s = Scene::from_json(old).unwrap();
        assert_eq!(s.default.effect, Effect::Breathing);
        assert_eq!(s.default.color, "123456");
        let new = serde_json::to_string(&s).unwrap();
        assert_eq!(Scene::from_json(&new).unwrap(), s);
    }

    #[test]
    fn profiles_round_trip_and_off_stops_animation() {
        let mut s = Scene::default();
        s.apply("d", st("ff0000"));
        let saved = s.lighting();
        s.apply("all", st("00ff00"));
        s.off = true;
        s.load(&saved);
        assert!(!s.off);
        assert_eq!(s.effective("d").color, "ff0000");
        s.default.effect = Effect::Spectrum;
        assert!(s.is_animated());
        s.off = true;
        assert!(!s.is_animated());
    }

    #[test]
    fn zone_ids_use_names() {
        let ids = zone_ids(
            "d",
            ["ARGB_V2_1", "Motherboard", "ARGB_V2_1", "Top/Front", "7"],
        );
        assert_eq!(
            ids,
            [
                "d/ARGB_V2_1",
                "d/Motherboard",
                "d/ARGB_V2_1#2",
                "d/Top-Front",
                "d/zone-7"
            ]
        );
    }

    #[test]
    fn rename_moves_overrides_layouts_groups_and_profiles() {
        let mut s = Scene::default();
        let layout = FanLayout {
            fans: 2,
            leds_per_fan: 8,
        };
        s.layouts.insert("d/1".into(), layout);
        s.overrides.insert("d/1".into(), st("111111"));
        s.overrides.insert("d/1/0".into(), st("222222"));
        s.overrides.insert("d/10".into(), st("333333"));
        s.overrides.insert("d/A/1".into(), st("444444"));
        s.overrides.insert("d/1/1".into(), st("555555"));
        s.groups.push(Group {
            id: "g1".into(),
            name: "Fans".into(),
            members: vec!["d/1/0".into(), "d/10".into()],
        });
        s.profiles.insert("p".into(), s.lighting());
        assert!(s.rename_target("d/1", "d/A"));
        assert_eq!(s.layouts["d/A"], layout);
        assert_eq!(s.overrides["d/A"].color, "111111");
        assert_eq!(s.overrides["d/A/0"].color, "222222");
        assert_eq!(s.overrides["d/A/1"].color, "444444", "the new id wins");
        assert_eq!(s.overrides["d/10"].color, "333333", "d/10 is another zone");
        assert!(!s.overrides.contains_key("d/1/1"));
        assert_eq!(s.groups[0].members, ["d/A/0", "d/10"]);
        assert_eq!(s.profiles["p"].overrides["d/A/0"].color, "222222");
        assert!(!s.rename_target("d/1", "d/A"), "nothing left to move");
    }

    #[test]
    fn hidden_devices_and_triggers_persist() {
        let mut s = Scene::default();
        s.hidden.insert("openrgb:Razer Mouse".into());
        s.triggers.push(
            serde_json::from_str(r#"{"id":"t1","name":"Beat","source":{"kind":"audio_beat"},"action":{"kind":"pulse","duration_ms":200},"targets":["d/1"]}"#)
                .unwrap(),
        );
        let back = Scene::from_json(&serde_json::to_string(&s).unwrap()).unwrap();
        assert_eq!(back, s);
        assert!(back.is_hidden("openrgb:Razer Mouse") && !back.is_hidden("openrgb:Board"));
        // Older files have neither.
        let old = Scene::from_json(r#"{"default":{"effect":"static","color":"ff0000","brightness":50,"speed":50}}"#).unwrap();
        assert!(old.hidden.is_empty() && old.triggers.is_empty());
        // Trigger targets follow renamed zones.
        assert!(s.rename_target("d/1", "d/A"));
        assert_eq!(s.triggers[0].targets, ["d/A"]);
    }

    #[test]
    fn animated_if_any_part_is() {
        let mut s = Scene::default();
        assert!(!s.is_animated());
        s.overrides.insert(
            "x".into(),
            LightState {
                effect: Effect::Spectrum,
                ..Default::default()
            },
        );
        assert!(s.is_animated());
    }
}
