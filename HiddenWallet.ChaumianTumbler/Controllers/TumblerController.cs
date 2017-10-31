﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using HiddenWallet.SharedApi.Models;
using HiddenWallet.ChaumianTumbler.Models;
using HiddenWallet.ChaumianCoinJoin;
using Org.BouncyCastle.Utilities.Encoders;
using NBitcoin;
using NBitcoin.RPC;
using HiddenWallet.ChaumianTumbler.Store;
using HiddenWallet.ChaumianTumbler.Clients;
using Nito.AsyncEx;

namespace HiddenWallet.ChaumianTumbler.Controllers
{
	[Route("api/v1/[controller]")]
	public class TumblerController : Controller
    {
		[HttpGet]
		public string Test()
		{
			return "test";
		}

		//	Used to simulate a client connecting. Returns ClientTest.html which contains JavaScript
		//	to connect to the SignalR Hub - ChaumianTumblerHub. In real use the clients will connect 
		//	directly to the hub via C# code in HiddenWallet.
		[Route("client-test")]
		[HttpGet]
		public IActionResult ClientTest()
		{
			return View();
		}

		[Route("client-test-broadcast")]
		[HttpGet]
		public void TestBroadcast()
		{
			//	Trigger a call to the hub to broadcast a new state to the clients.
			TumblerPhaseBroadcaster tumblerPhaseBroadcast = TumblerPhaseBroadcaster.Instance;

			PhaseChangeBroadcast broadcast = new PhaseChangeBroadcast { NewPhase = TumblerPhase.OutputRegistration.ToString(), Message = "Just a test" };
			tumblerPhaseBroadcast.Broadcast(broadcast); //If collection.Count > 3 a SignalR broadcast is made to clients that connected via client-test
		}
		
		[Route("status")]
		[HttpGet]
		public IActionResult Status()
		{
			try
			{
				return new JsonResult(new StatusResponse
				{
					Phase = Global.StateMachine.Phase.ToString(),
					Denomination = Global.StateMachine.Denomination.ToString(fplus: false, trimExcessZero: true),
					AnonymitySet = Global.StateMachine.AnonymitySet,
					TimeSpentInInputRegistrationInSeconds = (int)Global.StateMachine.TimeSpentInInputRegistration.TotalSeconds,
					MaximumInputsPerAlices = (int)Global.Config.MaximumInputsPerAlices,
					FeePerInputs = Global.StateMachine.FeePerInputs.ToString(fplus: false, trimExcessZero: true),
					FeePerOutputs = Global.StateMachine.FeePerOutputs.ToString(fplus: false, trimExcessZero: true),
					Version = "v1" // client should check and if the version is newer then client should update its software
				});
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		private readonly AsyncLock InputRegistrationLock = new AsyncLock();
		[Route("inputs")]
		[HttpPost]
		public async Task<IActionResult> InputsAsync(InputsRequest request)
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.InputRegistration || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				// Check not nulls
				if (request.BlindedOutput == null) return new BadRequestResult();
				if (request.ChangeOutput == null) return new BadRequestResult();
				if (request.Inputs == null || request.Inputs.Count() == 0) return new BadRequestResult();

				// Check format (parse everyting))
				byte[] blindedOutput = HexHelpers.GetBytes(request.BlindedOutput);
				Network network = Global.Config.Network;
				var changeOutput = new BitcoinWitPubKeyAddress(request.ChangeOutput, expectedNetwork: network);
				if (request.Inputs.Count() > Global.Config.MaximumInputsPerAlices) throw new NotSupportedException("Too many inputs provided");
				var inputs = new HashSet<(TxOut Output, OutPoint OutPoint)>();

				using (await InputRegistrationLock.LockAsync())
				{
					foreach (InputProofModel input in request.Inputs)
					{
						var op = new OutPoint();
						op.FromHex(input.Input);
						if (inputs.Any(x => x.OutPoint.Hash == op.Hash && x.OutPoint.N == op.N))
						{
							throw new ArgumentException("Attempting to register an input twice is not permitted");
						}
						if (Global.StateMachine.Alices.SelectMany(x => x.Inputs).Any(x => x.OutPoint.Hash == op.Hash && x.OutPoint.N == op.N))
						{
							throw new ArgumentException("Input is already registered by another Alice");
						}

						var txOutResponse = await Global.RpcClient.SendCommandAsync(RPCOperations.gettxout, op.Hash.ToString(), op.N, true);
						// Check if inputs are unspent
						if (txOutResponse.Result == null)
						{
							throw new ArgumentException("Provided input is not unspent");
						}
						// Check if inputs are confirmed or part of previous CoinJoin
						if (txOutResponse.Result.Value<int>("confirmations") <= 0)
						{
							if (!Global.CoinJoinStore.Transactions
								.Any(x => x.State == CoinJoinTransactionState.Succeeded && x.Transaction.GetHash() == op.Hash))
							{
								throw new ArgumentException("Provided input is not confirmed, nor spends a previous CJ transaction");
							}
						}
						// Check if inputs are native segwit
						if (txOutResponse.Result["scriptPubKey"].Value<string>("type") != "witness_v0_keyhash")
						{
							throw new ArgumentException("Provided input is not witness_v0_keyhash");
						}
						var value = txOutResponse.Result.Value<decimal>("value");
						var scriptPubKey = new Script(txOutResponse.Result["scriptPubKey"].Value<string>("asm"));
						var address = (BitcoinWitPubKeyAddress)scriptPubKey.GetDestinationAddress(network);
						// Check if proofs are valid
						if (!address.VerifyMessage(request.BlindedOutput, input.Proof))
						{
							throw new ArgumentException("Provided proof is invalid");
						}
						var txout = new TxOut(new Money(value, MoneyUnit.BTC), scriptPubKey);
						inputs.Add((txout, op));
					}

					// Check if inputs have enough coins
					Money amount = Money.Zero;
					foreach (Money val in inputs.Select(x => x.Output.Value))
					{
						amount += val;
					}
					Money feeToPay = (inputs.Count() * Global.StateMachine.FeePerInputs + 2 * Global.StateMachine.FeePerOutputs);
					if (amount < Global.StateMachine.Denomination + feeToPay)
					{
						throw new ArgumentException("Total provided inputs must be > denomination + fee");
					}

					byte[] signature = Global.RsaKey.SignBlindedData(blindedOutput);
					Guid uniqueId = Guid.NewGuid();

					var alice = new Alice
					{
						UniqueId = uniqueId,
						ChangeOutput = changeOutput,
						State = AliceState.InputsRegistered
					};
					foreach (var input in inputs)
					{
						alice.Inputs.Add(input);
					}
					Global.StateMachine.Alices.Add(alice);

					if (Global.StateMachine.Alices.Count >= Global.StateMachine.AnonymitySet)
					{
						Global.StateMachine.UpdatePhase(TumblerPhase.ConnectionConfirmation);
					}

					return new ObjectResult(new InputsResponse()
					{
						UniqueId = uniqueId.ToString(),
						SignedBlindedOutput = HexHelpers.ToString(signature)
					});
				}
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("input-registration-status")]
		[HttpGet]
		public IActionResult InputRegistrationStatus()
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.InputRegistration || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				return new ObjectResult(new InputRegistrationStatusResponse()
				{
					ElapsedSeconds = (int)Global.StateMachine.InputRegistrationStopwatch.Elapsed.TotalSeconds,
					RequiredPeerCount = Global.StateMachine.AnonymitySet,
					RegisteredPeerCount = Global.StateMachine.Alices.Count
				});
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("connection-confirmation")]
		[HttpPost]
		public IActionResult ConnectionConfirmation(ConnectionConfirmationRequest request)
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.ConnectionConfirmation || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				if (request.UniqueId == null) return new BadRequestResult();
				Alice alice = FindAlice(request.UniqueId);

				if (alice.State == AliceState.ConnectionConfirmed)
				{
					throw new InvalidOperationException("Connection is already confirmed");
				}

				alice.State = AliceState.ConnectionConfirmed;

				if(Global.StateMachine.Alices.All(x=>x.State == AliceState.ConnectionConfirmed))
				{
					Global.StateMachine.UpdatePhase(TumblerPhase.OutputRegistration);
				}

				return new ObjectResult(new SuccessResponse());
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("output")]
		[HttpPost]
		public IActionResult Output(OutputRequest request)
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.OutputRegistration || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				if (request.SignedOutput == null) return new BadRequestResult();

				throw new NotImplementedException();
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("coinjoin")]
		[HttpGet]
		public IActionResult CoinJoin(CoinJoinRequest request)
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.Signing || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				if (request.UniqueId == null) return new BadRequestResult();
				Alice alice = FindAlice(request.UniqueId);

				throw new NotImplementedException();
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("signature")]
		[HttpPost]
		public IActionResult Signature(SignatureRequest request)
		{
			try
			{
				if (Global.StateMachine.Phase != TumblerPhase.Signing || !Global.StateMachine.AcceptRequest)
				{
					return new ObjectResult(new FailureResponse { Message = "Wrong phase", Details = "" });
				}

				if (request.UniqueId == null) return new BadRequestResult();
				if (request.Signature == null) return new BadRequestResult();
				Alice alice = FindAlice(request.UniqueId);

				throw new NotImplementedException();
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		private static Alice FindAlice(string uniqueId)
		{
			Alice alice = Global.StateMachine.Alices.FirstOrDefault(x => x.UniqueId == new Guid(uniqueId));
			if (alice == default(Alice))
			{
				throw new ArgumentException("Wrong uniqueId");
			}

			return alice;
		}
	}
}