using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MLAPI.Cryptography.KeyExchanges;
using MLAPI.Transport.ChaCha20.ChaCha20;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;

namespace MLAPI.Transport.ChaCha20
{
    public class CryptographyTransportAdapter : NetworkTransport
    {
        public override ulong ServerClientId => Transport.ServerClientId;

        public NetworkTransport Transport;

        public bool SignKeyExchange;

        [TextArea]
        public string ServerBase64PFX;

        private X509Certificate2 m_ServerCertificate;

        private byte[] m_ServerCertificateBytes
        {
            get
            {
                if (m_ServerCertificatesByteBacking == null)
                {
                    m_ServerCertificatesByteBacking = m_ServerCertificate.Export(X509ContentType.Cert);
                }

                return m_ServerCertificatesByteBacking;
            }
        }

        private byte[] m_ServerCertificatesByteBacking;

        // State
        private bool m_IsServer;

        // Used by client
        private ECDiffieHellmanRSA m_ServerSignedKeyExchange;
        private ECDiffieHellman m_ServerKeyExchange;

        // Used by server
        private readonly Dictionary<ulong, ECDiffieHellmanRSA> m_ClientSignedKeyExchanges = new Dictionary<ulong, ECDiffieHellmanRSA>();
        private readonly Dictionary<ulong, ECDiffieHellman> m_ClientKeyExchanges = new Dictionary<ulong, ECDiffieHellman>();

        public byte[] ServerKey { get; private set; }
        public readonly Dictionary<ulong, byte[]> ClientKeys = new Dictionary<ulong, byte[]>();

        private readonly Dictionary<ulong, ChaCha20Cipher> m_ClientCiphers = new Dictionary<ulong, ChaCha20Cipher>();
        private ChaCha20Cipher m_ServerCipher;

        private readonly Dictionary<ulong, ClientState> m_ClientStates = new Dictionary<ulong, ClientState>();

        private enum ClientState : byte
        {
            WaitingForHailResponse,
            Connected
        }

        // Max message size
        private readonly byte[] m_CryptoBuffer = new byte[1024 * 8];
        private readonly byte[] m_WriteBuffer = new byte[1024 * 8];

        private enum MessageType : byte
        {
            Hail, // Server->Client
            HailResponse, // Client->Server
            Ready, // Server->Client
            Internal // MLAPI Message
        }

        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery networkDelivery)
        {
            // Write message type
            m_WriteBuffer[0] = (byte) MessageType.Internal;

            // Get the ChaCha20 cipher
            ChaCha20Cipher cipher = clientId == ServerClientId ? m_ServerCipher : m_ClientCiphers[clientId];

            // Encrypt with ChaCha
            cipher.ProcessBytes(data.Array, data.Offset, m_WriteBuffer, 1, data.Count);

            // Send the encrypted format
            Transport.Send(clientId, new ArraySegment<byte>(m_WriteBuffer, 0, 1 + data.Count), networkDelivery);
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            NetworkEvent @event = Transport.PollEvent(out ulong internalClientId, out ArraySegment<byte> internalPayload, out float internalReceiveTime);

            if (@event == NetworkEvent.Connect && m_IsServer && !m_ClientStates.ContainsKey(internalClientId))
            {
                // Write message type
                m_WriteBuffer[0] = (byte)((byte)MessageType.Hail | (SignKeyExchange ? (byte)1 : (byte)0) << 7);

                int length = 0;

                if (SignKeyExchange)
                {
                    // Create handshake parameters
                    ECDiffieHellmanRSA keyExchange = new ECDiffieHellmanRSA(m_ServerCertificate);
                    m_ClientSignedKeyExchanges.Add(internalClientId, keyExchange);

                    // Write public part length
                    m_WriteBuffer[1] = (byte) m_ServerCertificateBytes.Length;
                    m_WriteBuffer[2] = (byte) (m_ServerCertificateBytes.Length >> 8);

                    // Write public part of RSA key
                    Buffer.BlockCopy(m_ServerCertificateBytes, 0, m_WriteBuffer, 3, m_ServerCertificateBytes.Length);

                    // Get the secure public part (semi heavy)
                    byte[] securePublic = keyExchange.GetSecurePublicPart();

                    // Write public part length
                    m_WriteBuffer[3 + m_ServerCertificateBytes.Length] = (byte) securePublic.Length;
                    m_WriteBuffer[4 + m_ServerCertificateBytes.Length] = (byte) (securePublic.Length >> 8);

                    // Write key exchange public part
                    Buffer.BlockCopy(securePublic, 0, m_WriteBuffer, 5 + m_ServerCertificateBytes.Length, securePublic.Length);

                    // Set length
                    length = 5 + m_ServerCertificateBytes.Length + securePublic.Length;
                }
                else
                {
                    // Create handshake parameters
                    ECDiffieHellman keyExchange = new ECDiffieHellman();
                    m_ClientKeyExchanges.Add(internalClientId, keyExchange);

                    // Get the secure public part (semi heavy)
                    byte[] publicKey = keyExchange.GetPublicKey();

                    // Write public part length
                    m_WriteBuffer[1] = (byte) publicKey.Length;
                    m_WriteBuffer[2] = (byte) (publicKey.Length >> 8);

                    // Write key exchange public part
                    Buffer.BlockCopy(publicKey, 0, m_WriteBuffer, 3, publicKey.Length);

                    // Set length
                    length = 3 + publicKey.Length;
                }

                // Ensure length is set
                Assert.IsTrue(length != 0);

                // Send hail
                Transport.Send(internalClientId, new ArraySegment<byte>(m_WriteBuffer, 0, length), NetworkDelivery.ReliableSequenced);

                // Add them to client state
                m_ClientStates.Add(internalClientId, ClientState.WaitingForHailResponse);

                clientId = internalClientId;
                payload = new ArraySegment<byte>();
                receiveTime = internalReceiveTime;
                return NetworkEvent.Nothing;
            }
            else if (@event == NetworkEvent.Data)
            {
                // TODO: IGNORE SIGN BIT

                // Keep track of a read head
                int position = internalPayload.Offset;
                int start = position;

                MessageType messageType = (MessageType) (internalPayload.Array[position++] & 0x7F);

                if (messageType == MessageType.Hail && !m_IsServer)
                {
                    // Server sent us a hail

                    // Read if the data was signed
                    bool sign = ((internalPayload.Array[start] & 0x80) >> 7) == 1;

                    if (sign != SignKeyExchange)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                        {
                            NetworkLog.LogErrorServer("Mismatch between " + nameof(SignKeyExchange));
                        }

                        clientId = internalClientId;
                        payload = new ArraySegment<byte>();
                        receiveTime = internalReceiveTime;
                        return NetworkEvent.Nothing;
                    }

                    if (SignKeyExchange)
                    {
                        // Read certificate length
                        ushort certLength = (ushort)(internalPayload.Array[position++] | (ushort)(internalPayload.Array[position++] << 8));

                        // Alloc cert
                        // Cert needs exact buffer size so we cannot reuse
                        byte[] cert = new byte[certLength];

                        // Copy cert into cert buffer
                        Buffer.BlockCopy(internalPayload.Array, position += certLength, cert, 0, certLength);

                        // Create cert
                        m_ServerCertificate = new X509Certificate2(cert);

                        // TODO: IMPORTANT!!! VERIFY CERTIFICATE!!!!!!!

                        // Create key exchange
                        m_ServerSignedKeyExchange = new ECDiffieHellmanRSA(m_ServerCertificate);

                        // Read public part length
                        ushort publicPartLength = (ushort)(internalPayload.Array[position++] | (ushort)(internalPayload.Array[position++] << 8));

                        // Read servers public part length
                        byte[] serverPublicPart = new byte[publicPartLength];

                        // Copy public part
                        Buffer.BlockCopy(internalPayload.Array, position += publicPartLength, serverPublicPart, 0, publicPartLength);

                        // Get shared
                        byte[] key = m_ServerSignedKeyExchange.GetVerifiedSharedPart(serverPublicPart);

                        // Do key stretching with PBKDF2-HMAC-SHA1 with Application.productName as salt
                        using (Rfc2898DeriveBytes pbdkf = new Rfc2898DeriveBytes(key, Encoding.UTF8.GetBytes(Application.productName), 10_000))
                        {
                            // Add raw key for external use
                            ServerKey = key;

                            // ChaCha wants 48 bytes
                            byte[] chaChaData = pbdkf.GetBytes(48);

                            // Get key part
                            byte[] chaChaKey = new byte[32];
                            Buffer.BlockCopy(chaChaData, 0, chaChaKey, 0, 32);

                            // Get nonce part
                            byte[] chaChaNonce = new byte[12];
                            Buffer.BlockCopy(chaChaData, 32, chaChaNonce, 0, 12);

                            // Create cipher
                            m_ServerCipher = new ChaCha20Cipher(chaChaKey, chaChaNonce, BitConverter.ToUInt32(chaChaData, 32 + 12));
                        }
                    }
                    else
                    {
                        // Create key exchange
                        m_ServerKeyExchange = new ECDiffieHellman();

                        // Read public part length
                        ushort publicPartLength = (ushort)(internalPayload.Array[position++] | (ushort)(internalPayload.Array[position++] << 8));

                        // Read servers public part length
                        byte[] serverPublicPart = new byte[publicPartLength];

                        // Copy buffer
                        Buffer.BlockCopy(internalPayload.Array, position, serverPublicPart, 0, publicPartLength);

                        // Get shared
                        byte[] key = m_ServerKeyExchange.GetSharedSecretRaw(serverPublicPart);

                        // Do key stretching with PBKDF2-HMAC-SHA1 with Application.productName as salt
                        using (Rfc2898DeriveBytes pbdkf = new Rfc2898DeriveBytes(key, Encoding.UTF8.GetBytes(Application.productName), 10_000))
                        {
                            // Add raw key for external use
                            ServerKey = key;

                            // ChaCha wants 48 bytes
                            byte[] chaChaData = pbdkf.GetBytes(48);

                            // Get key part
                            byte[] chaChaKey = new byte[32];
                            Buffer.BlockCopy(chaChaData, 0, chaChaKey, 0, 32);

                            // Get nonce part
                            byte[] chaChaNonce = new byte[12];
                            Buffer.BlockCopy(chaChaData, 32, chaChaNonce, 0, 12);

                            // Create cipher
                            m_ServerCipher = new ChaCha20Cipher(chaChaKey, chaChaNonce, BitConverter.ToUInt32(chaChaData, 32 + 12));
                        }
                    }

                    /* Respond with hail response */

                    // Write message type
                    m_WriteBuffer[0] = (byte) MessageType.HailResponse;

                    // Get public part to write
                    byte[] publicPart = SignKeyExchange ? m_ServerSignedKeyExchange.GetSecurePublicPart() : m_ServerKeyExchange.GetPublicKey();

                    // Write public part length
                    m_WriteBuffer[1] = (byte) publicPart.Length;
                    m_WriteBuffer[2] = (byte) (publicPart.Length >> 8);

                    // Write public part
                    Buffer.BlockCopy(publicPart, 0, m_WriteBuffer, 3, publicPart.Length);

                    // Send hail response
                    Transport.Send(internalClientId, new ArraySegment<byte>(m_WriteBuffer, 0, 3 + publicPart.Length), NetworkDelivery.ReliableSequenced);

                    clientId = internalClientId;
                    payload = new ArraySegment<byte>();
                    receiveTime = internalReceiveTime;
                    return NetworkEvent.Nothing;
                }
                else if (messageType == MessageType.HailResponse && m_IsServer && m_ClientStates.ContainsKey(internalClientId) && m_ClientStates[internalClientId] == ClientState.WaitingForHailResponse)
                {
                    // Client sent us a hail response

                    // Read clients public part
                    ushort clientPublicPartLength = (ushort)(internalPayload.Array[position++] | (ushort)(internalPayload.Array[position++] << 8));

                    // Alloc public part
                    byte[] clientPublicPart = new byte[clientPublicPartLength];

                    // Copy public part
                    Buffer.BlockCopy(internalPayload.Array, position, clientPublicPart, 0, clientPublicPartLength);

                    if (SignKeyExchange)
                    {
                        // Get key
                        byte[] key = m_ClientSignedKeyExchanges[internalClientId].GetVerifiedSharedPart(clientPublicPart);

                        // Do key stretching with PBKDF2-HMAC-SHA1 with Application.productName as salt
                        using (Rfc2898DeriveBytes pbdkf = new Rfc2898DeriveBytes(key, Encoding.UTF8.GetBytes(Application.productName), 10_000))
                        {
                            // Add raw key for external use
                            ClientKeys.Add(internalClientId, key);

                            // ChaCha wants 48 bytes
                            byte[] chaChaData = pbdkf.GetBytes(48);

                            // Get key part
                            byte[] chaChaKey = new byte[32];
                            Buffer.BlockCopy(chaChaData, 0, chaChaKey, 0, 32);

                            // Get nonce part
                            byte[] chaChaNonce = new byte[12];
                            Buffer.BlockCopy(chaChaData, 32, chaChaNonce, 0, 12);

                            // Create cipher
                            m_ClientCiphers.Add(internalClientId, new ChaCha20Cipher(chaChaKey, chaChaNonce, BitConverter.ToUInt32(chaChaData, 32 + 12)));
                        }

                        // Cleanup
                        m_ClientSignedKeyExchanges.Remove(internalClientId);
                    }
                    else
                    {
                        // Get key
                        byte[] key = m_ClientKeyExchanges[internalClientId].GetSharedSecretRaw(clientPublicPart);

                        // Do key stretching with PBKDF2-HMAC-SHA1 with Application.productName as salt
                        using (Rfc2898DeriveBytes pbdkf = new Rfc2898DeriveBytes(key, Encoding.UTF8.GetBytes(Application.productName), 10_000))
                        {
                            // Add raw key for external use
                            ClientKeys.Add(internalClientId, key);

                            // ChaCha wants 48 bytes
                            byte[] chaChaData = pbdkf.GetBytes(48);

                            // Get key part
                            byte[] chaChaKey = new byte[32];
                            Buffer.BlockCopy(chaChaData, 0, chaChaKey, 0, 32);

                            // Get nonce part
                            byte[] chaChaNonce = new byte[12];
                            Buffer.BlockCopy(chaChaData, 32, chaChaNonce, 0, 12);

                            // Create cipher
                            m_ClientCiphers.Add(internalClientId, new ChaCha20Cipher(chaChaKey, chaChaNonce, BitConverter.ToUInt32(chaChaData, 32 + 12)));
                        }

                        //Cleanup
                        m_ClientKeyExchanges.Remove(internalClientId);
                    }

                    /* Respond with ready response */

                    // Write message type
                    m_WriteBuffer[0] = (byte) MessageType.Ready;

                    // Send ready message
                    Transport.Send(internalClientId, new ArraySegment<byte>(m_WriteBuffer, 0, 1), NetworkDelivery.ReliableSequenced);

                    // Elevate to connected
                    m_ClientStates[internalClientId] = ClientState.Connected;

                    clientId = internalClientId;
                    payload = new ArraySegment<byte>();
                    receiveTime = internalReceiveTime;
                    return NetworkEvent.Connect;
                }
                else if (messageType == MessageType.Ready && !m_IsServer)
                {
                    // Server is ready for us!
                    // Let the MLAPI know we are connected
                    clientId = internalClientId;
                    payload = new ArraySegment<byte>();
                    receiveTime = internalReceiveTime;
                    return NetworkEvent.Connect;
                }
                else if (messageType == MessageType.Internal && (!m_IsServer || (m_ClientStates.ContainsKey(internalClientId) && m_ClientStates[internalClientId] == ClientState.Connected)))
                {
                    // Decrypt and pass message to the MLAPI

                    // Get the correct cipher
                    ChaCha20Cipher cipher = m_IsServer ? m_ClientCiphers[internalClientId] : m_ServerCipher;

                    // Decrypt bytes
                    cipher.ProcessBytes(internalPayload.Array, position, m_CryptoBuffer, 0, internalPayload.Count - position);

                    clientId = internalClientId;
                    payload = new ArraySegment<byte>(m_CryptoBuffer, 0, internalPayload.Count - position);
                    receiveTime = internalReceiveTime;
                    return NetworkEvent.Data;
                }
            }
            else if (@event == NetworkEvent.Disconnect)
            {
                // Cleanup

                if (m_IsServer)
                {
                    if (SignKeyExchange)
                    {
                        if (m_ClientSignedKeyExchanges.ContainsKey(internalClientId))
                        {
                            m_ClientSignedKeyExchanges.Remove(internalClientId);
                        }
                    }
                    else
                    {
                        if (m_ClientKeyExchanges.ContainsKey(internalClientId))
                        {
                            m_ClientKeyExchanges.Remove(internalClientId);
                        }
                    }

                    if (ClientKeys.ContainsKey(internalClientId))
                    {
                        ClientKeys.Remove(internalClientId);
                    }

                    if (m_ClientCiphers.ContainsKey(internalClientId))
                    {
                        m_ClientCiphers[internalClientId].Dispose();
                        m_ClientCiphers.Remove(internalClientId);
                    }

                    if (m_ClientStates.ContainsKey(internalClientId))
                    {
                        m_ClientStates.Remove(internalClientId);
                    }
                }
                else
                {
                    m_ServerSignedKeyExchange = null;
                    m_ServerKeyExchange = null;
                    ServerKey = null;
                }

                clientId = internalClientId;
                payload = new ArraySegment<byte>();
                receiveTime = internalReceiveTime;
                return NetworkEvent.Disconnect;
            }

            clientId = internalClientId;
            payload = new ArraySegment<byte>();
            receiveTime = 0;
            return NetworkEvent.Nothing;
        }

        public override bool StartClient()
        {
            m_IsServer = false;
            return Transport.StartClient();
        }

        public override bool StartServer()
        {
            m_IsServer = true;
            ParsePFX();
            return Transport.StartServer();
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            Transport.DisconnectRemoteClient(clientId);
        }

        public override void DisconnectLocalClient()
        {
            Transport.DisconnectLocalClient();
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            return Transport.GetCurrentRtt(clientId);
        }

        public override void Shutdown()
        {
            Transport.Shutdown();
        }

        public override void Initialize()
        {
            Transport.Initialize();
        }

        private void ParsePFX()
        {
            try
            {
                string pfx = ServerBase64PFX.Trim();

                try
                {
                    if (m_IsServer && SignKeyExchange && !string.IsNullOrWhiteSpace(pfx))
                    {
                        byte[] decodedPfx = Convert.FromBase64String(ServerBase64PFX);

                        m_ServerCertificate = new X509Certificate2(decodedPfx);

                        if (!m_ServerCertificate.HasPrivateKey)
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                            {
                                NetworkLog.LogWarningServer("The imported PFX file did not have a private key");
                            }
                        }
                    }
                }
                catch (FormatException e)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogErrorServer("Parsing PFX failed: " + e);
                    }
                }
            }
            catch (CryptographicException e)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                {
                    NetworkLog.LogErrorServer("Importing of certificate failed: " + e);
                }
            }
        }
    }
}