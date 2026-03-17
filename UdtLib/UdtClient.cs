using System.Net;
using UdtLib.Session;

namespace UdtLib;

public static class UdtClient
{
    public static IUdtSession Connect(IPEndPoint remote) => UdtSession.CreateClient(remote);
}
