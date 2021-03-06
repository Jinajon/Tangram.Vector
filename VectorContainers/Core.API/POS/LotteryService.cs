﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.API.Extentions;
using Core.API.Model;
using libsignal.ecc;
using Microsoft.Extensions.Logging;

namespace Core.API.POS
{
    public class LotteryService<TAttach> : ILotteryService
    {
        private readonly IUnitOfWork unitOfWork;
        private readonly ILogger logger;
        private readonly IBaseGraphRepository<TAttach> baseGraphRepository;

        private readonly List<LotteryTicketProto> tmpList = new List<LotteryTicketProto>();

        public LotteryService(IUnitOfWork unitOfWork, ILogger<LotteryService<TAttach>> logger)
        {
            this.unitOfWork = unitOfWork;
            this.logger = logger;

            baseGraphRepository = unitOfWork.CreateBaseGraphOf<TAttach>();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public ECKeyPair GenerateKeyPair()
        {
            return Curve.generateKeyPair();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="lotteryTicket"></param>
        public void Register(LotteryTicketProto lotteryTicket)
        {
            tmpList.Add(lotteryTicket);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task<LotteryWinnerProto> PickWinner()
        {
            var graphProto = await baseGraphRepository.GetLast();

            var tickets = tmpList.Select(x => x.SerialNumber).ToArray();
            var keyPair = GenerateKeyPair();
            var provableMessage = string.Join(".", tickets) + $".{graphProto.Block.SignedBlock.Signature}";

            var proof = Curve.calculateVrfSignature(keyPair.getPrivateKey(), provableMessage.ToBytes());
            var vrfBytes = Curve.verifyVrfSignature(keyPair.getPublicKey(), provableMessage.ToBytes(), proof);

            var winners = PickRandomParticipants(tickets, vrfBytes);

            var lotteryWinner = new LotteryWinnerProto
            {
                BlockHeight = graphProto.Block.SignedBlock.Height,
                Message = provableMessage,
                Proof = proof.ToHex(),
                Vrf = vrfBytes.ToHex(),
                Winner = winners[0],
                Witnesses = winners.Skip(1).ToArray(),
                PublicKey = keyPair.getPublicKey().serialize().ToHex()
            };

            var signature = Curve.calculateSignature(keyPair.getPrivateKey(), Helper.Util.SerializeProto(lotteryWinner));

            lotteryWinner.Signature = signature.ToHex();
            return lotteryWinner;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="lotteryWinner"></param>
        /// <returns></returns>
        public async Task<bool> VerifyWinner(LotteryWinnerProto lotteryWinner)
        {
            try
            {
                var loadedBlockSignature = await baseGraphRepository.GetFirstOrDefault(x => x.Block.SignedBlock.Height == lotteryWinner.BlockHeight);
                var blockSignature = lotteryWinner.Message.Substring(lotteryWinner.Message.LastIndexOf(".", StringComparison.CurrentCulture), loadedBlockSignature.Block.SignedBlock.Signature.Length);

                if (loadedBlockSignature.Block.SignedBlock.Signature != blockSignature)
                {
                    throw new Exception($"Provable signature contains different block signature at height {loadedBlockSignature.Block.SignedBlock.Height}");
                }

                var ecPubKey = Curve.decodePoint(lotteryWinner.PublicKey.FromHex(), 0);
                var vrfBytes = Curve.verifyVrfSignature(ecPubKey, lotteryWinner.Message.ToBytes(), lotteryWinner.Proof.FromHex());

                var provableMessage = lotteryWinner.Message.Substring(0, lotteryWinner.Message.Length + 1 - blockSignature.Length);
                var participants = provableMessage.Split('.').Select(x => x);

                var winners = PickRandomParticipants(participants.ToArray(), vrfBytes);
            }
            catch (Exception ex)
            {
                logger.LogError($"<<< LotteryProvider.VerifyWinner >>>: {ex.ToString()}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="participants"></param>
        /// <param name="seed"></param>
        /// <returns></returns>
        public string[] PickRandomParticipants(string[] participants, byte[] seed)
        {
            var winners = new string[participants.Length];
            participants.CopyTo(winners, 0);

            KnuthShuffle(winners, seed);

            return winners;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array"></param>
        /// <param name="seed"></param>
        private static void KnuthShuffle<T>(T[] array, byte[] seed)
        {
            var secureRandom = new Org.BouncyCastle.Security.SecureRandom();
            secureRandom.SetSeed(seed);

            for (int i = 0; i < array.Length; i++)
            {
                int j = secureRandom.Next(i, array.Length); // Don't select from the entire array on subsequent loops
                T temp = array[i]; array[i] = array[j]; array[j] = temp;
            }
        }
    }
}
