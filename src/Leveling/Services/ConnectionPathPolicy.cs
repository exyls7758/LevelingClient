using System;
using System.Net;
using System.Net.Sockets;

namespace Multiverse.Services;

internal enum ConnectionPath { Existing, Tailscale, LocalDirect }

internal static class ConnectionPathPolicy
{
    internal static bool IsTailscale(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
    internal static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
    internal static void Validate(ConnectionPath path, IPAddress address)
    {
        if (path == ConnectionPath.Tailscale && !IsTailscale(address))
            throw new InvalidOperationException("Esse endereço não é do Tailscale. Use o endereço 100.x do computador autorizado ou selecione Direto na rede local.");
        if (path == ConnectionPath.LocalDirect && !IsPrivateLan(address))
            throw new InvalidOperationException("Direto local requer um IPv4 privado da rede confiável, como 192.168.x.x. Para redes diferentes, use Tailscale. P2P pela internet ainda não está disponível.");
    }
    internal static string Describe(IPAddress? address) => address is null ? "Sem sessão" :
        IsTailscale(address) ? "Tailscale · rota direta/relay a verificar" :
        IsPrivateLan(address) ? "Direto · rede privada" : IPAddress.IsLoopback(address) ? "Loopback · teste local" : "Endereço externo · transporte legado";
}
