using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace metalslugvoice
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

        // ───────── 메인 리로드 액션 / 무기 상태 ─────────
        private CA_Reload _mainReload;
        private float _nextReloadSearchTime;

        private UnityEngine.Object _lastGunAgent;
        private WeaponVoiceClass _lastGunClass = WeaponVoiceClass.None;
        private float _nextGunCheckTime;

        private const float ReloadSearchInterval = 1.0f;  // 1초마다 CA_Reload 탐색
        private const float GunCheckInterval = 0.15f; // 0.15초마다 현재 무기 체크

        // AR 무기 TypeID 목록 – 238 포함 (네가 준 값)
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
            1362,
        };

        // 샷건 무기 TypeID 목록 (네가 준 값)
        private readonly HashSet<int> _shotgunTypeIds = new HashSet<int>
        {
            248,
            250,
            657,
            658,
            876,
            1089,
        };

        // 스나이퍼 무기 TypeID 목록 (네가 준 값)
        private readonly HashSet<int> _sniperTypeIds = new HashSet<int>
        {
            246,
            407,
            437,
            780,
            781,
            782,
        };

        private enum WeaponVoiceClass
        {
            None = 0,
            AR = 1,
            Shotgun = 2,
            Sniper = 3,
        }

        // Item / ItemEntry 타입별 TypeID 멤버 캐시
        private readonly Dictionary<Type, MemberInfo> _itemTypeIdMemberCache =
            new Dictionary<Type, MemberInfo>();

        // CA_Reload → characterController 필드 캐시
        private FieldInfo _actionCharacterControllerField;

        // characterController → agentHolder 필드 캐시
        private FieldInfo _controllerAgentHolderField;

        // agentHolder → CurrentHoldGun 멤버 캐시
        private PropertyInfo _holderCurrentGunProp;
        private FieldInfo _holderCurrentGunField;

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
        // UnityEngine.Object 파괴 감지 (씬 이동 등)
        // ─────────────────────────────────────────
        private bool IsUnityObjectDestroyed(object obj)
        {
            UnityEngine.Object u = obj as UnityEngine.Object;
            return u != null && u == null;
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

        private void PlayHeavyMachineGun()
        {
            PlayVoice("heavy_machine_gun.wav");
        }

        private void PlayShotgun()
        {
            PlayVoice("shotgun.wav");
        }

        private void PlayArmorPiercer()
        {
            PlayVoice("armor_piercer.wav");
        }

        private void PlayGrenade()
        {
            PlayVoice("grenade.wav");
        }

        // ─────────────────────────────────────────
        // Update 루프
        // ─────────────────────────────────────────

        private void Update()
        {
            // 맵 이동 등으로 리로드 액션이 파괴되었으면 초기화
            if (_mainReload != null && IsUnityObjectDestroyed(_mainReload))
            {
                Debug.Log("[msvoice] 메인 리로드 액션 파괴 감지 → 참조 초기화");
                _mainReload = null;
                _lastGunAgent = null;
                _lastGunClass = WeaponVoiceClass.None;
                _actionCharacterControllerField = null;
                _controllerAgentHolderField = null;
                _holderCurrentGunProp = null;
                _holderCurrentGunField = null;
                _nextReloadSearchTime = 0f;
            }

            // F10: 테스트용
            if (UnityEngine.Input.GetKeyDown(KeyCode.F10))
            {
                Debug.Log("[msvoice] F10 테스트 재생");
                PlayHeavyMachineGun();
            }

            // 퀵슬롯 키 (3~8) → Grenade!
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

            // ① 메인 CA_Reload 찾기
            if (_mainReload == null && Time.unscaledTime >= _nextReloadSearchTime)
            {
                _nextReloadSearchTime = Time.unscaledTime + ReloadSearchInterval;
                TryResolveMainReload();
            }

            // ② 현재 무기 체크 (내 캐릭터 한 명만)
            if (_mainReload != null && Time.unscaledTime >= _nextGunCheckTime)
            {
                _nextGunCheckTime = Time.unscaledTime + GunCheckInterval;
                CheckCurrentGunAndPlayVoice();
            }
        }

        // ─────────────────────────────────────────
        // 메인 리로드 액션 찾기 (CA_Reload 기반)
        // ─────────────────────────────────────────

        private void TryResolveMainReload()
        {
            try
            {
                CA_Reload[] reloads = UnityEngine.Object.FindObjectsOfType<CA_Reload>();
                if (reloads == null || reloads.Length == 0)
                {
                    return;
                }

                CA_Reload fallback = null;

                for (int i = 0; i < reloads.Length; i++)
                {
                    CA_Reload action = reloads[i];
                    if (action == null) continue;

                    if (fallback == null)
                        fallback = action;

                    object ctrl = GetCharacterControllerFromAction(action);
                    if (ctrl == null) continue;

                    if (IsMainCharacter(ctrl))
                    {
                        _mainReload = action;
                        Debug.Log("[msvoice] 메인 리로드 액션 확보: " +
                                  action.GetType().FullName + " / " +
                                  action.gameObject.name);
                        return;
                    }
                }

                // IsMainCharacter 정보를 못 찾았으면 첫 번째라도 사용
                if (fallback != null)
                {
                    _mainReload = fallback;
                    Debug.Log("[msvoice] 메인 플래그는 없지만 첫 CA_Reload 사용: " +
                              fallback.GetType().FullName + " / " +
                              fallback.gameObject.name);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] TryResolveMainReload 예외: " + ex);
            }
        }

        private object GetCharacterControllerFromAction(CA_Reload action)
        {
            if (action == null) return null;

            if (_actionCharacterControllerField == null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type t = typeof(CA_Reload);
                FieldInfo field = null;

                while (t != null && field == null)
                {
                    field =
                        t.GetField("characterController", flags) ??
                        t.GetField("CharacterController", flags);
                    t = t.BaseType;
                }

                _actionCharacterControllerField = field;
            }

            if (_actionCharacterControllerField == null) return null;

            try
            {
                return _actionCharacterControllerField.GetValue(action);
            }
            catch
            {
                return null;
            }
        }

        private bool IsMainCharacter(object controller)
        {
            if (controller == null) return false;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = controller.GetType();

            try
            {
                // Property 우선
                PropertyInfo prop =
                    t.GetProperty("IsMainCharacter", flags) ??
                    t.GetProperty("isMainCharacter", flags) ??
                    t.GetProperty("IsLocalPlayer", flags) ??
                    t.GetProperty("isLocalPlayer", flags) ??
                    t.GetProperty("IsMine", flags);

                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    bool value = (bool)prop.GetValue(controller, null);
                    return value;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] IsMainCharacter(Prop) 예외: " + ex);
            }

            try
            {
                FieldInfo field =
                    t.GetField("IsMainCharacter", flags) ??
                    t.GetField("isMainCharacter", flags) ??
                    t.GetField("IsLocalPlayer", flags) ??
                    t.GetField("isLocalPlayer", flags);

                if (field != null && field.FieldType == typeof(bool))
                {
                    bool value = (bool)field.GetValue(controller);
                    return value;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] IsMainCharacter(Field) 예외: " + ex);
            }

            return false;
        }

        // ─────────────────────────────────────────
        // characterController → CurrentHoldGun 얻기
        // ─────────────────────────────────────────

        private UnityEngine.Object GetCurrentGunAgentFromReload()
        {
            if (_mainReload == null) return null;

            object controller = GetCharacterControllerFromAction(_mainReload);
            if (controller == null) return null;

            // controller → agentHolder
            if (_controllerAgentHolderField == null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type t = controller.GetType();
                FieldInfo field = null;

                while (t != null && field == null)
                {
                    field =
                        t.GetField("agentHolder", flags) ??
                        t.GetField("AgentHolder", flags);
                    t = t.BaseType;
                }

                _controllerAgentHolderField = field;
            }

            if (_controllerAgentHolderField == null) return null;

            object holder;
            try
            {
                holder = _controllerAgentHolderField.GetValue(controller);
            }
            catch
            {
                return null;
            }

            if (holder == null) return null;

            // holder → CurrentHoldGun
            if (_holderCurrentGunProp == null && _holderCurrentGunField == null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type ht = holder.GetType();

                _holderCurrentGunProp =
                    ht.GetProperty("CurrentHoldGun", flags) ??
                    ht.GetProperty("currentHoldGun", flags);

                if (_holderCurrentGunProp == null)
                {
                    _holderCurrentGunField =
                        ht.GetField("CurrentHoldGun", flags) ??
                        ht.GetField("currentHoldGun", flags);
                }
            }

            object gunObj = null;

            try
            {
                if (_holderCurrentGunProp != null)
                {
                    gunObj = _holderCurrentGunProp.GetValue(holder, null);
                }
                else if (_holderCurrentGunField != null)
                {
                    gunObj = _holderCurrentGunField.GetValue(holder);
                }
            }
            catch
            {
                return null;
            }

            UnityEngine.Object uo = gunObj as UnityEngine.Object;
            if (uo != null && uo == null)
            {
                return null; // 파괴된 총기
            }

            return uo;
        }

        // ─────────────────────────────────────────
        // 현재 무기 변경 감지 + 보이스 재생
        // ─────────────────────────────────────────

        private void CheckCurrentGunAndPlayVoice()
        {
            UnityEngine.Object gunAgent = GetCurrentGunAgentFromReload();
            if (gunAgent == null)
            {
                _lastGunAgent = null;
                _lastGunClass = WeaponVoiceClass.None;
                return;
            }

            // 에이전트 인스턴스가 바뀐 경우만 "무기 교체"로 봄
            if (gunAgent == _lastGunAgent)
            {
                return;
            }

            _lastGunAgent = gunAgent;

            object itemEntry = GetItemFromAgent(gunAgent);
            if (itemEntry == null)
            {
                _lastGunClass = WeaponVoiceClass.None;
                return;
            }

            int? typeId = GetTypeIdFromItemEntry(itemEntry);
            if (!typeId.HasValue)
            {
                _lastGunClass = WeaponVoiceClass.None;
                return;
            }

            WeaponVoiceClass newClass = GetWeaponVoiceClass(typeId.Value);

            Debug.Log("[msvoice] 현재 무기 교체 감지: gun=" +
                      gunAgent.name + " typeId=" + typeId.Value +
                      " class=" + newClass);

            _lastGunClass = newClass;

            switch (newClass)
            {
                case WeaponVoiceClass.AR:
                    PlayHeavyMachineGun();
                    break;
                case WeaponVoiceClass.Shotgun:
                    PlayShotgun();
                    break;
                case WeaponVoiceClass.Sniper:
                    PlayArmorPiercer();
                    break;
            }
        }

        private WeaponVoiceClass GetWeaponVoiceClass(int typeId)
        {
            if (_arTypeIds.Contains(typeId)) return WeaponVoiceClass.AR;
            if (_shotgunTypeIds.Contains(typeId)) return WeaponVoiceClass.Shotgun;
            if (_sniperTypeIds.Contains(typeId)) return WeaponVoiceClass.Sniper;
            return WeaponVoiceClass.None;
        }

        // ─────────────────────────────────────────
        // ItemAgent_Gun 등 → Item 찾기 (깊이 제한 재귀)
        // ─────────────────────────────────────────

        private object GetItemFromAgent(UnityEngine.Object agent)
        {
            if (agent == null) return null;

            try
            {
                HashSet<object> visited = new HashSet<object>();
                return FindItemEntryFromObjectInternal(agent, 0, 3, visited);
            }
            catch (Exception ex)
            {
                Debug.Log("[msvoice] GetItemFromAgent 예외: " + ex);
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
            string name = t.Name ?? string.Empty;

            if (ns.Contains("ItemStatsSystem")) return true;
            if (name.Contains("Item") ||
                name.Contains("ItemEntry") ||
                name.Contains("ItemSetting") ||
                name.Contains("Inventory"))
            {
                return true;
            }

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


