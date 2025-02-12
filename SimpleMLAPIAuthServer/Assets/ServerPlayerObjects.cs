﻿using MLAPI;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Takes player cmds and applies then to physics objects, transmits state of physics objects to clients
public class ServerPlayerObjects : NetworkedBehaviour
{
    ClientPlayerObjects clientPlayerObjects;

    // How fast we send out state of all players to all clients
    static readonly uint PlayerStatesSendRate = 20; // Per second

    static readonly float turnSpeed = 3.0f;
    static readonly float moveDirectionSpeed = 8.0f;
    static readonly float runSpeed = 10.0f;
    static readonly float jumpSpeed = 8.0f;
    static readonly float gravitySpeed = 20.0f;
    static readonly float horizVertSpeed = 0.005f;

    // Server remembers all client physic objects here
    CustomTypes.PlayerObjectDict playerObjectDict = new CustomTypes.PlayerObjectDict();

    float lastPlayerStatesSendTime = 0.0f;
    float lastCmdIndexIncTime = 0.0f;
    bool cmdIndexInc = false;
    uint seq = 0;

    void ClientDisconnected(ulong clientId)
    {
        playerObjectDict.Remove(clientId);
    }

    void Start()
    {
        clientPlayerObjects = GetComponent<ClientPlayerObjects>();
        NetworkingManager.Singleton.OnClientDisconnectCallback += ClientDisconnected;
    }

    static public void Move(CustomTypes.PlayerObject playerObject, CustomTypes.PlayerCmd playerCmd)
    {
        if (playerCmd.horizontal > 0)
            playerObject.horizontal += horizVertSpeed;
        else if (playerCmd.horizontal < 0)
            playerObject.horizontal -= horizVertSpeed;
        else
            playerObject.horizontal = 0.0f;
        if (playerCmd.vertical > 0)
            playerObject.vertical += horizVertSpeed;
        else if (playerCmd.vertical < 0)
            playerObject.vertical -= horizVertSpeed;
        else
            playerObject.vertical = 0.0f;
        playerObject.horizontal = Mathf.Clamp(playerObject.horizontal, -1.0f, 1.0f);
        playerObject.vertical = Mathf.Clamp(playerObject.vertical, -1.0f, 1.0f);

        playerObject.obj.transform.Rotate(0, playerObject.horizontal * turnSpeed, 0); // Turn left/right

        // Only allow user control when on ground
        if (playerObject.controller.isGrounded)
        {
            if (playerCmd.mouseButton1)
            {
                if (playerCmd.mouseButton0)
                    playerObject.vertical = 1; // Move player forward if both buttons down
                playerObject.moveDirection = new Vector3(playerObject.horizontal, 0, playerObject.vertical); // Strafe
            }
            else
                playerObject.moveDirection = Vector3.forward * playerObject.vertical; // Move forward/backward

            playerObject.moveDirection = playerObject.obj.transform.TransformDirection(playerObject.moveDirection);
            playerObject.moveDirection *= moveDirectionSpeed;
            if (playerCmd.leftShiftKey)
                playerObject.moveDirection *= runSpeed;
            if (playerCmd.jumpButton)
                playerObject.moveDirection.y = jumpSpeed;
        }

        playerObject.moveDirection.y -= gravitySpeed * Time.deltaTime; // Apply gravity
        playerObject.controller.Move(playerObject.moveDirection * Time.deltaTime);
    }

    void Update()
    {
        if (!IsServer)
        {
            playerObjectDict.Clear();
            return;
        }

        CustomTypes.PlayerObject playerObject;
        CustomTypes.PlayerCmdSet playerCmdSet;

        // Every 0.05 s we receive 5 cmds that were recorded 0.01 s apart
        // We send out a new player state every 0.05 s
        // Optimal: inc cmd index to next every 0.01 s and assume we receive the next 5 before (?) 0.05 s is up
        // Reality: let's inc only after 0.02 s have passed (last cmds will not be reached if we receive a new set)
        if (lastCmdIndexIncTime + (1.0f / HandlePlayerCmds.PlayerCmdsSendRate / (CustomTypes.PlayerCmdSet.Max / 2)) <= Time.time)
        {
            cmdIndexInc = true;
            lastCmdIndexIncTime = Time.time;
        }
        else
        {
            cmdIndexInc = false;
        }

        // Loop through collected player inputs and calc physics of move
        foreach (ulong clientId in HandlePlayerCmds.playerCmdsDict.Keys.ToList())
        {
            // Get player object representing these player cmds
            if (playerObjectDict.playerObjects.ContainsKey(clientId))
            {
                playerObject = playerObjectDict.playerObjects[clientId];
            }
            else
            {
                // Fix a bug in MLAPI where it doesn't call OnClientDisconnectCallback for Host
                if (!NetworkingManager.Singleton.ConnectedClients.ContainsKey(clientId))
                {
                    HandlePlayerCmds.playerCmdsDict.Remove(clientId);
                    continue;
                }

                // New player so create game object
                playerObject = new CustomTypes.PlayerObject();
                playerObject.obj = Instantiate(clientPlayerObjects.playerPrefab, clientPlayerObjects.playerStart.transform.position, clientPlayerObjects.playerStart.transform.rotation);
                playerObject.obj.layer = LayerMask.NameToLayer("Server");
                playerObject.obj.GetComponent<MeshRenderer>().material.color = Color.red;
                playerObject.obj.name = $"Server {playerObject.obj.name} {clientId}";
                playerObject.controller = playerObject.obj.AddComponent<CharacterController>(); // Needed for physics calc
                playerObject.moveDirection = Vector3.zero;
                playerObjectDict.playerObjects.Add(clientId, playerObject);
            }

            playerCmdSet = HandlePlayerCmds.playerCmdsDict[clientId];

            // Note: Remember index resets when new cmds come in
            if (cmdIndexInc)
            {
                playerCmdSet.cmdIndex++;
                if (playerCmdSet.cmdIndex == CustomTypes.PlayerCmdSet.Max)
                {
                    // Slow down state send rate? Speed up player cmd send rate?
                    Debug.LogWarning($"Ran out of cmds to use for client {clientId}");
                    playerCmdSet.cmdIndex = CustomTypes.PlayerCmdSet.Max - 1;
                }
            }

            Move(playerObject, playerCmdSet.playerCmds[playerCmdSet.cmdIndex]);
        }

        // Send out state of all objects?
        if (lastPlayerStatesSendTime + (1.0f / PlayerStatesSendRate) > Time.time)
            return;
        lastPlayerStatesSendTime = Time.time;

        // Create player states from dict
        // FIXME: Recreate player state list each time?
        List<CustomTypes.PlayerState> playerStates = new List<CustomTypes.PlayerState>();
        foreach (var item in playerObjectDict.playerObjects)
        {
            CustomTypes.PlayerState ps = new CustomTypes.PlayerState();
            ps.clientId = item.Key;
            ps.position = item.Value.obj.transform.position;
            ps.rotation = item.Value.obj.transform.rotation;
            playerStates.Add(ps);
        }
        if (playerStates.Count > 0)
        {
            // Send player states to connected clients
            clientPlayerObjects.InvokeClientRpcOnEveryone(clientPlayerObjects.ReceivePlayerStates, seq, playerStates);
            seq++;
        }
    }
}
