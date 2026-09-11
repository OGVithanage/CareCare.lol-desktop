#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <initguid.h>
#include <fwpmu.h>
#include <windns.h>
#include <algorithm>
#include <string>
#include <sstream>
#include <vector>

// Stable keys ensure recovery/uninstall only touches objects belonging to CareCare.
static const GUID Provider = {0x9c161a2e,0x41fd,0x47e3,{0xa8,0xcf,0x39,0xbb,0xde,0xd8,0x2b,0x61}};
static const GUID Sublayer = {0x7b4bb6e2,0xb7a0,0x4aa4,{0x8a,0x4f,0x3c,0x7e,0xca,0x21,0xd9,0x86}};
static void Check(DWORD result) { if (result != ERROR_SUCCESS) throw result; }
struct Engine {
    HANDLE handle = nullptr;
    Engine() { Check(FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &handle)); }
    ~Engine() { if (handle) FwpmEngineClose0(handle); }
};
struct Transaction {
    HANDLE engine;
    bool committed = false;
    explicit Transaction(HANDLE e) : engine(e) { Check(FwpmTransactionBegin0(e, 0)); }
    void Commit() { Check(FwpmTransactionCommit0(engine)); committed = true; }
    ~Transaction() { if (!committed) FwpmTransactionAbort0(engine); }
};
static void EnsureObjects(HANDLE engine) {
    FWPM_PROVIDER0 provider{};
    provider.providerKey = Provider;
    provider.displayData.name = const_cast<wchar_t*>(L"CareCare website allowlist");
    provider.flags = FWPM_PROVIDER_FLAG_PERSISTENT;
    // Associates persistent policy with the auto-start service.
    provider.serviceName = const_cast<wchar_t*>(L"CareCareAllowlist");
    DWORD result = FwpmProviderAdd0(engine, &provider, nullptr);
    if (result != FWP_E_ALREADY_EXISTS) Check(result);
    FWPM_SUBLAYER0 sublayer{};
    sublayer.subLayerKey = Sublayer;
    sublayer.providerKey = const_cast<GUID*>(&Provider);
    sublayer.displayData.name = provider.displayData.name;
    sublayer.flags = FWPM_SUBLAYER_FLAG_PERSISTENT;
    sublayer.weight = 0x8000;
    result = FwpmSubLayerAdd0(engine, &sublayer, nullptr);
    if (result != FWP_E_ALREADY_EXISTS) Check(result);
}
static void DeleteLayerFilters(HANDLE engine, const GUID& layer) {
    FWPM_FILTER_ENUM_TEMPLATE0 query{};
    query.providerKey = const_cast<GUID*>(&Provider);
    query.layerKey = layer;
    query.enumType = FWP_FILTER_ENUM_FULLY_CONTAINED;
    query.actionMask = 0xffffffff;
    query.flags = FWP_FILTER_ENUM_FLAG_INCLUDE_DISABLED;
    HANDLE enumeration = nullptr;
    Check(FwpmFilterCreateEnumHandle0(engine, &query, &enumeration));
    std::vector<UINT64> ids;
    try {
        for (;;) {
            FWPM_FILTER0** filters = nullptr;
            UINT32 count = 0;
            Check(FwpmFilterEnum0(engine, enumeration, 256, &filters, &count));
            for (UINT32 i = 0; i < count; ++i) ids.push_back(filters[i]->filterId);
            FwpmFreeMemory0(reinterpret_cast<void**>(&filters));
            if (count < 256) break;
        }
        for (auto id : ids) Check(FwpmFilterDeleteById0(engine, id));
    } catch (...) { FwpmFilterDestroyEnumHandle0(engine, enumeration); throw; }
    Check(FwpmFilterDestroyEnumHandle0(engine, enumeration));
}
static void DeleteFilters(HANDLE engine) {
    DeleteLayerFilters(engine, FWPM_LAYER_ALE_AUTH_CONNECT_V4);
    DeleteLayerFilters(engine, FWPM_LAYER_ALE_AUTH_CONNECT_V6);
}
static FWPM_FILTER_CONDITION0 Port(const GUID& key, UINT16 value) {
    FWPM_FILTER_CONDITION0 condition{};
    condition.fieldKey = key;
    condition.matchType = FWP_MATCH_EQUAL;
    condition.conditionValue.type = FWP_UINT16;
    condition.conditionValue.uint16 = value;
    return condition;
}
static void Add(HANDLE engine, bool ipv6, bool permit, std::vector<FWPM_FILTER_CONDITION0> conditions) {
    FWPM_FILTER0 filter{};
    Check(UuidCreate(&filter.filterKey));
    filter.displayData.name = const_cast<wchar_t*>(permit ? L"CareCare allow" : L"CareCare default deny");
    filter.providerKey = const_cast<GUID*>(&Provider);
    filter.subLayerKey = Sublayer;
    filter.layerKey = ipv6 ? FWPM_LAYER_ALE_AUTH_CONNECT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V4;
    filter.flags = FWPM_FILTER_FLAG_PERSISTENT;
    filter.action.type = permit ? FWP_ACTION_PERMIT : FWP_ACTION_BLOCK;
    UINT64 weight = permit ? 100 : 1;
    filter.weight.type = FWP_UINT64;
    filter.weight.uint64 = &weight;
    filter.numFilterConditions = static_cast<UINT32>(conditions.size());
    filter.filterCondition = conditions.data();
    Check(FwpmFilterAdd0(engine, &filter, nullptr, nullptr));
}
static void Address(HANDLE engine, const std::wstring& address, bool dns) {
    IN_ADDR v4{};
    IN6_ADDR v6{};
    FWP_BYTE_ARRAY16 bytes{};
    FWPM_FILTER_CONDITION0 condition{};
    condition.fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
    condition.matchType = FWP_MATCH_EQUAL;
    bool ipv6 = false;
    if (InetPtonW(AF_INET, address.c_str(), &v4) == 1) {
        condition.conditionValue.type = FWP_UINT32;
        condition.conditionValue.uint32 = ntohl(v4.S_un.S_addr);
    } else if (InetPtonW(AF_INET6, address.c_str(), &v6) == 1) {
        ipv6 = true;
        memcpy(bytes.byteArray16, &v6, 16);
        condition.conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
        condition.conditionValue.byteArray16 = &bytes;
    } else { throw static_cast<DWORD>(ERROR_INVALID_PARAMETER); }
    if (!dns) { Add(engine, ipv6, true, {condition}); return; }
    for (UINT8 protocol : {static_cast<UINT8>(IPPROTO_UDP), static_cast<UINT8>(IPPROTO_TCP)}) {
        FWPM_FILTER_CONDITION0 transport{};
        transport.fieldKey = FWPM_CONDITION_IP_PROTOCOL;
        transport.matchType = FWP_MATCH_EQUAL;
        transport.conditionValue.type = FWP_UINT8;
        transport.conditionValue.uint8 = protocol;
        Add(engine, ipv6, true, {condition, Port(FWPM_CONDITION_IP_REMOTE_PORT, 53), transport});
    }
}
static void Infrastructure(HANDLE engine, bool ipv6) {
    FWPM_FILTER_CONDITION0 loopback{};
    loopback.fieldKey = FWPM_CONDITION_FLAGS;
    loopback.matchType = FWP_MATCH_FLAGS_ALL_SET;
    loopback.conditionValue.type = FWP_UINT32;
    loopback.conditionValue.uint32 = FWP_CONDITION_FLAG_IS_LOOPBACK;
    Add(engine, ipv6, true, {loopback});
    FWPM_FILTER_CONDITION0 udp{};
    udp.fieldKey = FWPM_CONDITION_IP_PROTOCOL;
    udp.matchType = FWP_MATCH_EQUAL;
    udp.conditionValue.type = FWP_UINT8;
    udp.conditionValue.uint8 = IPPROTO_UDP;
    Add(engine, ipv6, true, {udp, Port(FWPM_CONDITION_IP_LOCAL_PORT, ipv6 ? 546 : 68),
        Port(FWPM_CONDITION_IP_REMOTE_PORT, ipv6 ? 547 : 67)});
}
extern "C" __declspec(dllexport) DWORD __cdecl ApplyPolicy(const wchar_t* addresses, const wchar_t* resolvers) noexcept {
    try {
        if (!addresses || !resolvers) return ERROR_INVALID_PARAMETER;
        Engine engine;
        Transaction transaction(engine.handle);
        EnsureObjects(engine.handle);
        DeleteFilters(engine.handle);
        for (bool ipv6 : {false, true}) {
            Add(engine.handle, ipv6, false, {});
            Infrastructure(engine.handle, ipv6);
        }
        std::wstring entry;
        std::wistringstream dns(resolvers);
        while (std::getline(dns, entry)) if (!entry.empty()) Address(engine.handle, entry, true);
        std::wistringstream allowed(addresses);
        while (std::getline(allowed, entry)) if (!entry.empty()) Address(engine.handle, entry, false);
        // Addition/removal at ALE triggers existing flows to reauthorize on their next packet.
        transaction.Commit();
        return ERROR_SUCCESS;
    } catch (DWORD error) { return error; } catch (...) { return ERROR_GEN_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __cdecl RemovePolicy() noexcept {
    try {
        Engine engine;
        Transaction transaction(engine.handle);
        DeleteFilters(engine.handle);
        DWORD result = FwpmSubLayerDeleteByKey0(engine.handle, &Sublayer);
        if (result != FWP_E_SUBLAYER_NOT_FOUND) Check(result);
        result = FwpmProviderDeleteByKey0(engine.handle, &Provider);
        if (result != FWP_E_PROVIDER_NOT_FOUND) Check(result);
        transaction.Commit();
        return ERROR_SUCCESS;
    } catch (DWORD error) { return error; } catch (...) { return ERROR_GEN_FAILURE; }
}
// The system resolver is used, with TTLs from both address and CNAME records.
extern "C" __declspec(dllexport) DWORD __cdecl ResolveDomain(const wchar_t* domain, wchar_t* output, DWORD capacity, DWORD* ttl) noexcept {
    try {
        if (!domain || !output || !ttl || capacity == 0) return ERROR_INVALID_PARAMETER;
        std::wstring addresses;
        *ttl = MAXDWORD;
        for (WORD type : {static_cast<WORD>(DNS_TYPE_A), static_cast<WORD>(DNS_TYPE_AAAA)}) {
            PDNS_RECORD records = nullptr;
            const auto result = DnsQuery_W(domain, type, DNS_QUERY_BYPASS_CACHE | DNS_QUERY_TREAT_AS_FQDN,
                nullptr, &records, nullptr);
            if (result != ERROR_SUCCESS && result != DNS_INFO_NO_RECORDS && result != DNS_ERROR_RCODE_NAME_ERROR) {
                if (records) DnsRecordListFree(records, DnsFreeRecordList);
                return result;
            }
            for (auto record = records; record; record = record->pNext) {
                if (record->Flags.S.Section != DnsSectionAnswer) continue;
                if (record->wType == DNS_TYPE_CNAME || record->wType == DNS_TYPE_A || record->wType == DNS_TYPE_AAAA)
                    *ttl = std::min(*ttl, record->dwTtl);
                wchar_t address[INET6_ADDRSTRLEN]{};
                if (record->wType == DNS_TYPE_A)
                    InetNtopW(AF_INET, &record->Data.A.IpAddress, address, INET6_ADDRSTRLEN);
                else if (record->wType == DNS_TYPE_AAAA)
                    InetNtopW(AF_INET6, &record->Data.AAAA.Ip6Address, address, INET6_ADDRSTRLEN);
                if (address[0]) { addresses += address; addresses += L'\n'; }
            }
            if (records) DnsRecordListFree(records, DnsFreeRecordList);
        }
        if (addresses.empty()) return DNS_INFO_NO_RECORDS;
        if (addresses.size() >= capacity) return ERROR_INSUFFICIENT_BUFFER;
        std::copy(addresses.begin(), addresses.end(), output);
        output[addresses.size()] = 0;
        return ERROR_SUCCESS;
    } catch (...) { return ERROR_GEN_FAILURE; }
}
