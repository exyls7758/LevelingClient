using Multiverse.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Multiverse.Services
{
    internal static class FriendStoreCodec
    {
        private sealed class PayloadDto
        {
            public int Version { get; set; }
            public List<FriendDto> Friends { get; set; } = new();
        }

        private sealed class FriendDto
        {
            public string DeviceId { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string PublicKeySpki { get; set; } = "";
            public string StablePeerId { get; set; } = "";
            public string TailscaleDnsName { get; set; } = "";
            public string LastKnownIp { get; set; } = "";
            public DateTimeOffset CreatedAtUtc { get; set; }
            public DateTimeOffset LastSeenUtc { get; set; }
            public bool? AllowDirectConnection { get; set; }
            public bool? AutoAcceptConnection { get; set; }
            public bool? AllowRemoteInput { get; set; }
            public bool? AllowRemoteAudio { get; set; }
            public bool? Blocked { get; set; }
        }

        internal static IReadOnlyList<MultiverseFriend> Decode(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<MultiverseFriend>();

            PayloadDto? payload = JsonSerializer.Deserialize<PayloadDto>(json);
            if (payload?.Friends is null)
                return Array.Empty<MultiverseFriend>();

            int version = payload.Version <= 0 ? 1 : payload.Version;
            var result = new List<MultiverseFriend>();
            foreach (FriendDto dto in payload.Friends)
            {
                if (string.IsNullOrWhiteSpace(dto.DeviceId) ||
                    string.IsNullOrWhiteSpace(dto.PublicKeySpki))
                {
                    continue;
                }

                bool direct = version >= 2 && dto.AllowDirectConnection == true;
                bool autoAccept = direct && version >= 2 && dto.AutoAcceptConnection == true;
                bool blocked = version >= 2 && dto.Blocked == true;
                bool input = version >= 2
                    ? dto.AllowRemoteInput ?? true
                    : true;
                bool audio = version >= 2
                    ? dto.AllowRemoteAudio ?? true
                    : true;

                if (blocked)
                {
                    direct = false;
                    autoAccept = false;
                }

                result.Add(new MultiverseFriend
                {
                    DeviceId = dto.DeviceId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? "Amigo" : dto.DisplayName.Trim(),
                    PublicKeySpki = dto.PublicKeySpki,
                    StablePeerId = dto.StablePeerId ?? "",
                    TailscaleDnsName = dto.TailscaleDnsName ?? "",
                    LastKnownIp = dto.LastKnownIp ?? "",
                    CreatedAtUtc = dto.CreatedAtUtc == default ? DateTimeOffset.UtcNow : dto.CreatedAtUtc,
                    LastSeenUtc = dto.LastSeenUtc == default ? DateTimeOffset.UtcNow : dto.LastSeenUtc,
                    AllowDirectConnection = direct,
                    AutoAcceptConnection = autoAccept,
                    AllowRemoteInput = input,
                    AllowRemoteAudio = audio,
                    Blocked = blocked
                });
            }

            return result;
        }

        internal static string Encode(IEnumerable<MultiverseFriend> friends)
        {
            var payload = new PayloadDto
            {
                Version = 2,
                Friends = friends.Select(friend => new FriendDto
                {
                    DeviceId = friend.DeviceId,
                    DisplayName = friend.DisplayName,
                    PublicKeySpki = friend.PublicKeySpki,
                    StablePeerId = friend.StablePeerId,
                    TailscaleDnsName = friend.TailscaleDnsName,
                    LastKnownIp = friend.LastKnownIp,
                    CreatedAtUtc = friend.CreatedAtUtc,
                    LastSeenUtc = friend.LastSeenUtc,
                    AllowDirectConnection = friend.AllowDirectConnection,
                    AutoAcceptConnection = friend.AutoAcceptConnection,
                    AllowRemoteInput = friend.AllowRemoteInput,
                    AllowRemoteAudio = friend.AllowRemoteAudio,
                    Blocked = friend.Blocked
                }).ToList()
            };
            return JsonSerializer.Serialize(payload);
        }
    }
}
