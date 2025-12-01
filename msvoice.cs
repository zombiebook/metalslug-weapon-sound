using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace msvoice
{
    // Duckov 모드 엔트리
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                Debug.Log("[msvoice] OnAfterSetup - 매니저 생성");

                GameObject root = new GameObject("MsVoiceRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<MsVoiceManager>();
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] OnAfterSetup 예외: " + ex);
            }
        }

        protected override void OnBeforeDeactivate()
        {
            Debug.Log("[msvoice] OnBeforeDeactivate");
        }
    }

    public class MsVoiceManager : MonoBehaviour
    {
        private static MsVoiceManager _instance;

        // ───────── 로컬 플레이어 추적 ─────────
        private Component _localPlayer;
        private Type _localPlayerType;
        private bool _searchedLocalPlayerOnce;
        private float _nextPlayerSearchTime;

        // ───────── ItemAgent_* 추적 ─────────
        private Type[] _itemAgentTypes;
        private bool _triedResolveItemAgentTypes;
        private float _nextWeaponScanTime;

        // 각 ItemAgent_* 인스턴스별 마지막 TypeID
        private readonly Dictionary<UnityEngine.Object, int> _lastGunTypeId =
            new Dictionary<UnityEngine.Object, int>();

        // AR 무기 TypeID 목록 – 238 포함 (당신이 준 값)
        private readonly HashSet<int> _arTypeIds = new HashSet<int>
        {
            238,
            240,
            242,
            244,
            256,
            654,
            659,
            681,
            682,
            862,
            1238,
        };

        // 샷건 무기 TypeID 목록 (당신이 준 값)
        private readonly HashSet<int> _shotgunTypeIds = new HashSet<int>
        {
            248,
            250,
            657,
            658,
            876,
            1089,
        };

        // 스나이퍼 무기 TypeID 목록 – 나중에 채울 수 있음
        private readonly HashSet<int> _sniperTypeIds = new HashSet<int>
        {
            246,
            407,
            437,
            780,
            781,
            782,
        };

        // 수류탄 TypeID 목록 – 빠른아이템 슬롯용 (지금은 비워두고 키 입력으로 처리)
        private readonly HashSet<int> _grenadeTypeIds = new HashSet<int>
        {
            // 예: 500, 501 ...
            // 수류탄 TypeID를 알게 되면 여기 추가
        };

        // 무기 보이스 분류
        private enum WeaponVoiceClass
        {
            None = 0,
            AR = 1,
            Shotgun = 2,
            Sniper = 3,
            Grenade = 4,
        }

        // Item / ItemEntry 타입별 TypeID 멤버 캐시
        private readonly Dictionary<Type, MemberInfo> _itemTypeIdMemberCache =
            new Dictionary<Type, MemberInfo>();

        private void Awake()
        {
            if (_instance != null)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            Debug.Log("[msvoice] MsVoiceManager.Start");
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // ─────────────────────────────────────────
        // 공통: 파일 이름으로 커스텀 사운드 재생
        // ─────────────────────────────────────────

        private void PlayVoice(string fileName)
        {
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string folder = System.IO.Path.GetDirectoryName(dllPath);
                string audioDir = System.IO.Path.Combine(folder, "Audio");
                string filePath = System.IO.Path.Combine(audioDir, fileName);

                if (!System.IO.File.Exists(filePath))
                {
                    Debug.Log("[msvoice] 사운드 파일 없음: " + filePath);
                    return;
                }

                Duckov.AudioManager.PostCustomSFX(filePath, null, false);
                Debug.Log("[msvoice] 사운드 재생: " + filePath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[msvoice] PlayVoice ERROR: " + ex);
            }
        }

        // Heavy Machine Gun!
        private void PlayHeavyMachineGun()
        {
            PlayVoice("heavy_machine_gun.wav");
        }

        // Shotgun!
        private void PlayShotgun()
        {
            PlayVoice("shotgun.wav");
        }

        // Armor Piercer!
        private void PlayArmorPiercer()
        {
            PlayVoice("armor_piercer.wav");
        }

        // Grenade!
        private void PlayGrenade()
        {
            PlayVoice("grenade.wav");
        }

        // ─────────────────────────────────────────
        // Update 루프
        // ─────────────────────────────────────────

        private void Update()
        {
            // ① F10: 항상 Heavy Machine Gun 테스트 재생
            if (UnityEngine.Input.GetKeyDown(KeyCode.F10))
            {
                Debug.Log("[msvoice] F10 테스트 재생");
                PlayHeavyMachineGun();
            }

            // ② 퀵슬롯 키 (3,4,5,6,7,8) 입력 시 Grenade! 보이스 재생
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha3) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Alpha4) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Alpha5) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Alpha6) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Alpha7) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Alpha8))
            {
                Debug.Log("[msvoice] 퀵슬롯 키(3~8) 입력 → Grenade 보이스 재생");
                PlayGrenade();
            }

            // ③ 로컬 플레이어 없으면 먼저 찾기
            if (_localPlayer == null)
            {
                TryFindLocalPlayerPeriodically();
                return;
            }

            // ④ 일정 주기로 ItemAgent_* 기반 무기/아이템 교체 이벤트 감지
            if (Time.unscaledTime >= _nextWeaponScanTime)
            {
                _nextWeaponScanTime = Time.unscaledTime + 0.2f; // 0.2초마다 가볍게 스캔
                EnsureItemAgentTypes();
                if (_itemAgentTypes != null && _itemAgentTypes.Length > 0)
                {
                    ScanItemAgents();
                }
            }
        }


        // ─────────────────────────────────────────
        // 로컬 플레이어 찾기
        // ─────────────────────────────────────────

        private void TryFindLocalPlayerPeriodically()
        {
            if (Time.unscaledTime < _nextPlayerSearchTime) return;
            _nextPlayerSearchTime = Time.unscaledTime + 1.0f;

            MonoBehaviour[] all = FindObjectsOfType<MonoBehaviour>();
            MonoBehaviour firstCharacter = null;

            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i];
                if (mb == null) continue;

                GameObject go = mb.gameObject;
                if (go == null) continue;

                string goName = go.name;
                if (string.IsNullOrEmpty(goName)) continue;

                if (!goName.Contains("Character") && !goName.Contains("Player"))
                {
                    continue;
                }

                if (firstCharacter == null)
                {
                    firstCharacter = mb;
                }

                Type t = mb.GetType();
                Debug.Log("[msvoice] 플레이어 후보 오브젝트 발견: " + goName + " / 컴포넌트=" + t.FullName);

                if (!IsLocalPlayer(mb, t))
                {
                    continue;
                }

                _localPlayer = mb;
                _localPlayerType = t;
                Debug.Log("[msvoice] 로컬 플레이어로 확정: " + goName + " / " + t.FullName);
                return;
            }

            if (_localPlayer == null && firstCharacter != null)
            {
                _localPlayer = firstCharacter;
                _localPlayerType = firstCharacter.GetType();
                Debug.Log("[msvoice] IsLocalPlayer 정보를 찾지 못해, 첫 Character/Player 오브젝트를 로컬 플레이어로 사용: "
                          + _localPlayer.gameObject.name + " / " + _localPlayerType.FullName);
                return;
            }

            if (_localPlayer == null && !_searchedLocalPlayerOnce)
            {
                Debug.Log("[msvoice] Character/Player 라는 이름을 가진 오브젝트를 찾지 못함");
                _searchedLocalPlayerOnce = true;
            }
        }

        private bool IsLocalPlayer(object player, Type t)
        {
            if (player == null || t == null) return false;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                PropertyInfo prop =
                    t.GetProperty("IsLocalPlayer", flags) ??
                    t.GetProperty("isLocalPlayer", flags) ??
                    t.GetProperty("IsMine", flags);

                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    bool value = (bool)prop.GetValue(player, null);
                    return value;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] IsLocalPlayer(Prop) 예외: " + ex);
            }

            try
            {
                FieldInfo field =
                    t.GetField("IsLocalPlayer", flags) ??
                    t.GetField("isLocalPlayer", flags);

                if (field != null && field.FieldType == typeof(bool))
                {
                    bool value = (bool)field.GetValue(player);
                    return value;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] IsLocalPlayer(Field) 예외: " + ex);
            }

            // 정보 없으면 일단 true
            return true;
        }

        // ─────────────────────────────────────────
        // ItemAgent_* 타입 찾기 (한 번만)
        // ─────────────────────────────────────────

        private void EnsureItemAgentTypes()
        {
            if ((_itemAgentTypes != null && _itemAgentTypes.Length > 0) || _triedResolveItemAgentTypes)
            {
                return;
            }

            _triedResolveItemAgentTypes = true;

            try
            {
                List<Type> found = new List<Type>();

                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly asm = asms[i];
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch
                    {
                        continue;
                    }

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type t = types[j];
                        if (t == null) continue;
                        if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;

                        string name = t.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!name.StartsWith("ItemAgent_", StringComparison.Ordinal)) continue;

                        found.Add(t);
                        Debug.Log("[msvoice] ItemAgent 타입 발견: " + t.FullName);
                    }
                }

                _itemAgentTypes = found.ToArray();

                if (_itemAgentTypes.Length == 0)
                {
                    Debug.Log("[msvoice] ItemAgent_* 타입을 찾지 못함");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] EnsureItemAgentTypes 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────
        // ItemAgent_* 기반 무기/아이템 교체 이벤트 감지
        // ─────────────────────────────────────────

        private void ScanItemAgents()
        {
            if (_itemAgentTypes == null || _itemAgentTypes.Length == 0) return;

            HashSet<UnityEngine.Object> seen = new HashSet<UnityEngine.Object>();

            for (int tIndex = 0; tIndex < _itemAgentTypes.Length; tIndex++)
            {
                Type agentType = _itemAgentTypes[tIndex];
                if (agentType == null) continue;

                UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(agentType);
                if (objs == null || objs.Length == 0) continue;

                for (int i = 0; i < objs.Length; i++)
                {
                    UnityEngine.Object obj = objs[i];
                    if (obj == null) continue;

                    if (!seen.Add(obj))
                    {
                        continue; // 이미 다른 타입에서 처리함
                    }

                    MonoBehaviour mb = obj as MonoBehaviour;
                    if (mb == null) continue;

                    object itemEntry = FindItemFromAgent(mb);
                    if (itemEntry == null) continue;

                    int? typeId = GetTypeIdFromItemEntry(itemEntry);
                    if (!typeId.HasValue) continue;

                    int lastType;
                    bool hadLast = _lastGunTypeId.TryGetValue(obj, out lastType);

                    WeaponVoiceClass newClass = GetWeaponVoiceClass(typeId.Value);
                    WeaponVoiceClass oldClass = hadLast ? GetWeaponVoiceClass(lastType) : WeaponVoiceClass.None;

                    if (newClass != WeaponVoiceClass.None && newClass != oldClass)
                    {
                        string goName = mb.gameObject != null ? mb.gameObject.name : "(no GameObject)";
                        Debug.Log("[msvoice] ItemAgent 무기/아이템 교체 감지: go=" + goName + " typeId=" + typeId.Value + " class=" + newClass + " → 보이스 재생");

                        if (newClass == WeaponVoiceClass.AR)
                        {
                            PlayHeavyMachineGun();
                        }
                        else if (newClass == WeaponVoiceClass.Shotgun)
                        {
                            PlayShotgun();
                        }
                        else if (newClass == WeaponVoiceClass.Sniper)
                        {
                            PlayArmorPiercer();
                        }
                        else if (newClass == WeaponVoiceClass.Grenade)
                        {
                            PlayGrenade();
                        }
                    }

                    _lastGunTypeId[obj] = typeId.Value;
                }
            }

            // 사라진 에이전트 정리
            if (_lastGunTypeId.Count > 0)
            {
                List<UnityEngine.Object> toRemove = null;
                foreach (KeyValuePair<UnityEngine.Object, int> kv in _lastGunTypeId)
                {
                    UnityEngine.Object key = kv.Key;
                    if (key == null || !seen.Contains(key))
                    {
                        if (toRemove == null) toRemove = new List<UnityEngine.Object>();
                        toRemove.Add(key);
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        _lastGunTypeId.Remove(toRemove[i]);
                    }
                }
            }
        }

        private WeaponVoiceClass GetWeaponVoiceClass(int typeId)
        {
            if (_arTypeIds.Contains(typeId))
                return WeaponVoiceClass.AR;
            if (_shotgunTypeIds.Contains(typeId))
                return WeaponVoiceClass.Shotgun;
            if (_sniperTypeIds.Contains(typeId))
                return WeaponVoiceClass.Sniper;
            if (_grenadeTypeIds.Contains(typeId))
                return WeaponVoiceClass.Grenade;
            return WeaponVoiceClass.None;
        }

        // ─────────────────────────────────────────
        // ItemAgent_* → ItemStatsSystem.Item 찾기 (깊이 제한 재귀)
        // ─────────────────────────────────────────

        private object FindItemFromAgent(MonoBehaviour agentMb)
        {
            if (agentMb == null) return null;

            try
            {
                HashSet<object> visited = new HashSet<object>();
                return FindItemEntryFromObjectInternal(agentMb, 0, 3, visited);
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] FindItemFromAgent 예외: " + ex);
                return null;
            }
        }

        private object FindItemEntryFromObjectInternal(object obj, int depth, int maxDepth, HashSet<object> visited)
        {
            if (obj == null) return null;
            if (depth > maxDepth) return null;

            if (obj is Item)
            {
                return obj;
            }

            if (visited.Contains(obj)) return null;
            visited.Add(obj);

            Type t = obj.GetType();
            if (IsTrivialType(t)) return null;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 필드
            try
            {
                FieldInfo[] fields = t.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.IsStatic) continue;

                    object value;
                    try
                    {
                        value = f.GetValue(obj);
                    }
                    catch
                    {
                        continue;
                    }

                    object found = FindItemEntryFromValue(value, depth, maxDepth, visited);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] FindItemEntryFromObjectInternal(Field) 예외: " + ex);
            }

            // 프로퍼티
            try
            {
                PropertyInfo[] props = t.GetProperties(flags);
                for (int i = 0; i < props.Length; i++)
                {
                    PropertyInfo p = props[i];
                    if (!p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;

                    object value;
                    try
                    {
                        value = p.GetValue(obj, null);
                    }
                    catch
                    {
                        continue;
                    }

                    object found = FindItemEntryFromValue(value, depth, maxDepth, visited);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] FindItemEntryFromObjectInternal(Property) 예외: " + ex);
            }

            return null;
        }

        private object FindItemEntryFromValue(object value, int depth, int maxDepth, HashSet<object> visited)
        {
            if (value == null) return null;

            if (value is Item)
            {
                return value;
            }

            Type vt = value.GetType();
            if (IsTrivialType(vt)) return null;

            // ItemStatsSystem 관련 타입이면 안쪽까지 더 파고 들어감
            if (IsItemRelatedType(vt))
            {
                object nested = FindItemEntryFromObjectInternal(value, depth + 1, maxDepth, visited);
                if (nested != null) return nested;
            }

            // 배열 / 리스트 등 컬렉션
            if (value is IEnumerable && !(value is string))
            {
                IEnumerable enumerable = (IEnumerable)value;
                foreach (object elem in enumerable)
                {
                    object nested = FindItemEntryFromValue(elem, depth + 1, maxDepth, visited);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        private bool IsTrivialType(Type t)
        {
            if (t == null) return true;
            if (t.IsPrimitive) return true;
            if (t.IsEnum) return true;
            if (t == typeof(string)) return true;
            return false;
        }

        private bool IsItemRelatedType(Type t)
        {
            if (t == null) return false;

            if (typeof(Item).IsAssignableFrom(t)) return true;

            string ns = t.Namespace ?? string.Empty;
            string name = t.Name;

            if (ns.Contains("ItemStatsSystem")) return true;
            if (name.Contains("Item") || name.Contains("ItemEntry") || name.Contains("ItemSetting") || name.Contains("Inventory"))
                return true;

            return false;
        }

        // ─────────────────────────────────────────
        // Item / ItemEntry → TypeID 추출
        // ─────────────────────────────────────────

        private int? GetTypeIdFromItemEntry(object itemEntry)
        {
            if (itemEntry == null) return null;

            Type t = itemEntry.GetType();
            MemberInfo cached;
            if (!_itemTypeIdMemberCache.TryGetValue(t, out cached))
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo prop =
                    t.GetProperty("TypeID", flags) ??
                    t.GetProperty("typeID", flags);

                if (prop != null && prop.PropertyType == typeof(int))
                {
                    cached = prop;
                }
                else
                {
                    FieldInfo field =
                        t.GetField("TypeID", flags) ??
                        t.GetField("typeID", flags);

                    if (field != null && field.FieldType == typeof(int))
                    {
                        cached = field;
                    }
                }

                _itemTypeIdMemberCache[t] = cached;
            }

            if (cached == null) return null;

            try
            {
                PropertyInfo prop = cached as PropertyInfo;
                if (prop != null)
                {
                    return (int)prop.GetValue(itemEntry, null);
                }

                FieldInfo field = cached as FieldInfo;
                if (field != null)
                {
                    return (int)field.GetValue(itemEntry);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] GetTypeIdFromItemEntry 예외: " + ex);
            }

            return null;
        }
    }
}
